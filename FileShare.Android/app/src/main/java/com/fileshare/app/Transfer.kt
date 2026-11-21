package com.fileshare.app

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import android.webkit.MimeTypeMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.File
import java.io.FileInputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.io.RandomAccessFile
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap

object Tx {
    var checkpointEvery = 64L shl 20 // 64 MB fsync + meta checkpoint
    var ackEvery = 8L shl 20        // 8 MB cumulative ack
    const val chunk = 1 shl 20      // 1 MB streaming buffer
    const val maxParallel = 4       // files in flight at once; each file stays one TCP stream
    const val maxAttempts = 6
}

enum class TState { PENDING, HASHING, CONNECTING, SENDING, RECONNECTING, PAUSED, DONE, FAILED, CANCELLED }

class TransferItem(val uri: Uri, val displayName: String, val size: Long, val relPath: String? = null) {
    @Volatile var state = TState.PENDING
    @Volatile var progress = 0L
    @Volatile var sha: String? = null
    @Volatile var error: String? = null
    val label: String get() = if (relPath != null) "$relPath/$displayName" else displayName
}

class HashMismatchException : IOException("hash mismatch")

fun readLine(input: InputStream): String {
    val out = ByteArrayOutputStream()
    while (true) {
        val b = input.read()
        if (b == -1) throw EOFException()
        if (b == 10) break
        out.write(b)
        if (out.size() > 4096) throw IOException("header too long")
    }
    return out.toString("UTF-8").trimEnd('\r')
}

fun writeLine(out: OutputStream, s: String) {
    out.write((s + "\n").toByteArray(Charsets.UTF_8))
    out.flush()
}

fun sha256(ctx: Context, uri: Uri): String {
    ctx.contentResolver.openInputStream(uri)!!.use { ins ->
        val md = MessageDigest.getInstance("SHA-256")
        val buf = ByteArray(Tx.chunk)
        while (true) {
            val n = ins.read(buf)
            if (n <= 0) break
            md.update(buf, 0, n)
        }
        return md.digest().joinToString("") { "%02x".format(it) }
    }
}

// Mirrors Sender in Transfer.cs: header → resume offset → streamed bytes → ok/false, retry with backoff.
object Sender {
    suspend fun send(ctx: Context, item: TransferItem, host: String, port: Int, onTick: () -> Unit) =
        withContext(Dispatchers.IO) {
            if (item.sha == null) {
                item.state = TState.HASHING
                onTick()
                item.sha = sha256(ctx, item.uri)
            }
            var backoff = 1000L
            var last: Exception? = null
            for (attempt in 1..Tx.maxAttempts) {
                item.state = if (attempt == 1) TState.CONNECTING else TState.RECONNECTING
                onTick()
                try {
                    Socket().use { s ->
                        s.soTimeout = 30_000
                        s.tcpNoDelay = true
                        s.sendBufferSize = 1 shl 20
                        s.connect(InetSocketAddress(host, port), 10_000)
                        val out = s.getOutputStream()
                        val input = BufferedInputStream(s.getInputStream(), 65536)
                        val headerJson = JSONObject()
                            .put("v", 2)
                            .put("name", item.displayName)
                            .put("size", item.size)
                            .put("sha", item.sha)
                        if (item.relPath != null) headerJson.put("path", item.relPath)
                        writeLine(out, headerJson.toString())
                        val resume = JSONObject(readLine(input)).optLong("resume", -1)
                        if (resume < 0) throw IOException("receiver busy with this file")
                        if (resume > item.size) throw IOException("receiver ahead of sender")
                        item.state = TState.SENDING
                        item.progress = resume
                        onTick()

                        val pfd = ctx.contentResolver.openFileDescriptor(item.uri, "r")
                            ?: throw IOException("cannot open file")
                        pfd.use { p ->
                            FileInputStream(p.fileDescriptor).use { fis ->
                                val ch = fis.channel // seekable — resumes from any offset
                                ch.position(resume)
                                val buf = ByteArray(Tx.chunk)
                                var sent = resume
                                var lastTick = 0L
                                while (true) {
                                    val n = ch.read(ByteBuffer.wrap(buf))
                                    if (n <= 0) break
                                    out.write(buf, 0, n)
                                    sent += n
                                    item.progress = sent
                                    val now = System.currentTimeMillis()
                                    if (now - lastTick >= 250) { // several files tick at once, do not re-render per chunk
                                        lastTick = now
                                        onTick()
                                    }
                                }
                            }
                        }
                        // drain replies; acks interleave with the final ok/false
                        while (true) {
                            val o = JSONObject(readLine(input))
                            if (o.has("ok")) {
                                if (!o.getBoolean("ok")) throw HashMismatchException()
                                break
                            }
                        }
                        item.state = TState.DONE
                        item.progress = item.size
                        onTick()
                        return@withContext
                    }
                } catch (e: CancellationException) {
                    item.state = TState.CANCELLED
                    onTick()
                    throw e
                } catch (e: HashMismatchException) {
                    item.state = TState.FAILED
                    item.error = "corrupted, resending starts over"
                    onTick()
                    throw e
                } catch (e: Exception) {
                    last = e
                    item.error = e.message
                    if (attempt == Tx.maxAttempts) break
                    delay(backoff)
                    backoff = (backoff * 2).coerceAtMost(30_000L)
                }
            }
            item.state = TState.FAILED
            onTick()
            throw last ?: IOException("transfer failed")
        }
}

// Mirrors Receiver in Transfer.cs: sequential write, fsync + meta checkpoints, hash verify, publish.
class Receiver(
    private val ctx: Context,
    private val onReceived: (String, Uri?) -> Unit,
    private val onProgress: (String, Long, Long) -> Unit = { _, _, _ -> },
    private val onEnd: (String) -> Unit = {},
) {
    private var server: ServerSocket? = null

    @Volatile
    var lastResumeOffset = 0L
        private set

    private val locks = ConcurrentHashMap<String, Any>()

    fun start(scope: CoroutineScope) {
        val ss = ServerSocket()
        ss.reuseAddress = true
        ss.bind(InetSocketAddress(Discovery.TCP_PORT))
        server = ss
        scope.launch(Dispatchers.IO) {
            while (true) {
                val c = try { ss.accept() } catch (e: Exception) { break }
                scope.launch(Dispatchers.IO) { handle(c) }
            }
        }
    }

    fun stop() {
        runCatching { server?.close() }
    }

    private fun receiveDir(): File = File(ctx.getExternalFilesDir(null), "FileShare").apply { mkdirs() }

    // sanitize a relative folder path from the wire: keep clean segments, drop traversal/drive parts
    private fun safeRel(rel: String): String =
        rel.split('/', '\\')
            .map { it.trim() }
            .filter { it.isNotEmpty() && it != "." && it != ".." && !it.endsWith(":") }
            .map { s -> "\\/:*?\"<>|".fold(s) { acc, c -> acc.replace(c, '_') } }
            .joinToString("/")

    private fun handle(client: Socket) {
        try {
            client.soTimeout = 30_000
            client.tcpNoDelay = true
            client.receiveBufferSize = 1 shl 20
            client.use { s ->
                val input = BufferedInputStream(s.getInputStream(), 65536)
                val out = s.getOutputStream()
                val header = JSONObject(readLine(input))
                val size = header.getLong("size")
                val sha = header.optString("sha")
                var name = header.optString("name").ifEmpty { "file" }
                    .substringAfterLast('/')
                    .substringAfterLast('\\')
                for (c in "\\/:*?\"<>|") name = name.replace(c, '_')
                val rel = safeRel(header.optString("path"))
                if (size < 0) return
                val label = if (rel.isEmpty()) name else "$rel/$name"

                // files from a transferred folder land in a matching subfolder; guard the canonical result
                val root = receiveDir().canonicalFile
                val dir = if (rel.isEmpty()) root
                else File(root, rel.replace('/', File.separatorChar)).canonicalFile
                    .takeIf { it.path.startsWith(root.path + File.separator) }
                    ?: root
                dir.mkdirs()
                val part = File(dir, "$name.part")
                val meta = File(dir, "$name.part.meta")

                val gate = locks.computeIfAbsent((if (rel.isEmpty()) "" else "$rel/") + name.lowercase()) { Any() }
                synchronized(gate) {
                    try {
                        var resume = 0L
                        if (part.exists() && meta.exists()) {
                            try {
                                val m = JSONObject(meta.readText())
                                if (m.getLong("size") == size && m.optString("sha") == sha) {
                                    val done = m.getLong("done")
                                    if (done in 0..size && part.length() >= done) resume = done
                                }
                            } catch (e: Exception) { /* corrupt meta → fresh start */ }
                        }
                        lastResumeOffset = resume
                        writeLine(out, JSONObject().put("resume", resume).toString())
                        onProgress(label, size, resume)

                        val md = MessageDigest.getInstance("SHA-256")
                        RandomAccessFile(part, "rw").use { raf ->
                            if (raf.length() < size) raf.setLength(size) // preallocate
                            raf.seek(resume)
                            if (resume > 0) {
                                // hash the trusted prefix already on disk
                                raf.seek(0)
                                val pb = ByteArray(Tx.chunk)
                                var left = resume
                                while (left > 0) {
                                    val n = raf.read(pb, 0, minOf(pb.size.toLong(), left).toInt())
                                    if (n <= 0) throw EOFException()
                                    md.update(pb, 0, n)
                                    left -= n
                                }
                                raf.seek(resume)
                            }

                            val ins = s.getInputStream()
                            val buf = ByteArray(Tx.chunk)
                            var received = resume
                            var lastCp = resume
                            var lastAck = resume
                            var lastReport = 0L
                            while (received < size) {
                                val n = ins.read(buf)
                                if (n <= 0) throw IOException("connection lost at $received/$size")
                                raf.write(buf, 0, n)
                                md.update(buf, 0, n)
                                received += n
                                val nowMs = System.currentTimeMillis()
                                if (nowMs - lastReport >= 250) {
                                    lastReport = nowMs
                                    onProgress(label, size, received)
                                }
                                if (received - lastCp >= Tx.checkpointEvery) {
                                    raf.fd.sync()
                                    writeMeta(meta, size, sha, received)
                                    lastCp = received
                                }
                                if (received - lastAck >= Tx.ackEvery) {
                                    writeLine(out, JSONObject().put("ack", received).toString())
                                    lastAck = received
                                }
                            }
                            raf.fd.sync()
                            writeMeta(meta, size, sha, received)
                        }

                        val digest = md.digest().joinToString("") { "%02x".format(it) }
                        if (digest != sha.lowercase()) {
                            part.delete()
                            meta.delete()
                            writeLine(out, JSONObject().put("ok", false).put("reason", "hash").toString())
                            return
                        }

                        val published = publish(part, name, rel)
                        if (published != null) {
                            part.delete()
                            meta.delete()
                        } else {
                            part.renameTo(File(dir, name))
                            meta.delete()
                        }
                        writeLine(out, JSONObject().put("ok", true).toString())
                        onReceived(label, published)
                    } finally {
                        onEnd(label)
                    }
                }
            }
        } catch (e: Exception) {
            // connection dropped → .part stays for resume
        }
    }

    private fun writeMeta(meta: File, size: Long, sha: String, done: Long) {
        val tmp = File(meta.parentFile, meta.name + ".tmp")
        tmp.writeText(JSONObject().put("size", size).put("sha", sha).put("done", done).toString())
        meta.delete()
        tmp.renameTo(meta)
    }

    // Move into the user-visible Downloads collection, under FileShare/<subfolder>; null → keep in the app folder.
    private fun publish(part: File, name: String, rel: String): Uri? {
        if (Build.VERSION.SDK_INT < 29) return null
        return try {
            val values = ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, name)
                put(MediaStore.MediaColumns.MIME_TYPE, mimeOf(name))
                put(MediaStore.MediaColumns.RELATIVE_PATH,
                    if (rel.isEmpty()) "Download/FileShare" else "Download/FileShare/$rel")
            }
            val uri = ctx.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return null
            ctx.contentResolver.openOutputStream(uri)!!.use { o -> part.inputStream().use { it.copyTo(o, Tx.chunk) } }
            uri
        } catch (e: Exception) {
            null
        }
    }

    private fun mimeOf(name: String): String {
        val ext = name.substringAfterLast('.', "").lowercase()
        return MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "application/octet-stream"
    }
}

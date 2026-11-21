package com.fileshare.app

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.DocumentsContract
import android.webkit.JavascriptInterface
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.app.ActivityCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

// The whole UI is assets/app.html rendered in a WebView; this activity is the bridge:
// JS calls Android.* for actions, Kotlin pushes full state as JSON into render(state).
class MainActivity : ComponentActivity() {
    private val main = Handler(Looper.getMainLooper())
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var selectedIp: String? = null
    private var fileCallback: ValueCallback<Array<Uri>>? = null
    private lateinit var webView: WebView

    private val pickTree = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { root ->
        if (root != null) Repo.pendingUris.value = Repo.pendingUris.value + walkTree(this, root)
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= 33) {
            ActivityCompat.requestPermissions(this, arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
        TransferService.start(this)
        handleShareIntent(intent)

        webView = WebView(this).apply {
            settings.javaScriptEnabled = true
            settings.allowFileAccess = true
            settings.useWideViewPort = true
            settings.loadWithOverviewMode = true
            addJavascriptInterface(Bridge(), "Android")
            webViewClient = object : WebViewClient() {
                override fun onPageFinished(view: WebView, url: String?) = pushNow()
            }
            webChromeClient = object : WebChromeClient() {
                // <input type="file" multiple> in the page becomes a document picker; URIs are staged directly.
                override fun onShowFileChooser(v: WebView, cb: ValueCallback<Array<Uri>>, p: FileChooserParams): Boolean {
                    fileCallback?.onReceiveValue(null)
                    fileCallback = cb
                    return try {
                        startActivityForResult(p.createIntent().apply { putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true) }, REQ_FILES)
                        true
                    } catch (e: Exception) {
                        fileCallback = null
                        false
                    }
                }
            }
            loadUrl("file:///android_asset/app.html")
        }
        setContentView(webView)

        // any Repo change re-renders the page
        listOf(Repo.peers, Repo.transfers, Repo.receiving, Repo.received, Repo.pendingUris).forEach { f ->
            scope.launch { f.collect { pushNow() } }
        }
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQ_FILES) return
        val uris = mutableListOf<Uri>()
        data?.clipData?.let { cd -> for (i in 0 until cd.itemCount) uris += cd.getItemAt(i).uri }
            ?: data?.data?.let { uris += it }
        if (uris.isNotEmpty()) Repo.pendingUris.value = Repo.pendingUris.value + uris.map { PendingPick(it, null) }
        fileCallback?.onReceiveValue(null) // URIs were staged directly; cancel the web-side pick
        fileCallback = null
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
        webView.destroy()
    }

    private fun pushNow() {
        if (!::webView.isInitialized) return
        val s = JSONObject()
            .put("selected", selectedIp ?: JSONObject.NULL)
            .put("peers", JSONArray().apply {
                Repo.peers.value.forEach { p ->
                    put(JSONObject().put("name", p.name).put("platform", p.platform).put("ip", p.ip))
                }
            })
            .put("pending", Repo.pendingUris.value.size)
            .put("transfers", JSONArray().apply {
                Repo.transfers.value.forEach { t ->
                    put(
                        JSONObject()
                            .put("label", t.label)
                            .put("state", t.state.name)
                            .put("progress", t.progress)
                            .put("size", t.size)
                            .put("error", t.error ?: JSONObject.NULL)
                            .put("active", t.state in activeStates),
                    )
                }
            })
            .put("recv", JSONArray().apply {
                Repo.receiving.value.forEach { r ->
                    put(JSONObject().put("name", r.name).put("size", r.size).put("got", r.received))
                }
            })
            .put("received", JSONArray().apply {
                Repo.received.value.forEach { f ->
                    put(JSONObject().put("name", f.name).put("at", f.at).put("open", f.uri != null))
                }
            })
        webView.evaluateJavascript("render($s)", null)
    }

    private inner class Bridge {
        @JavascriptInterface fun selectDevice(ip: String) = post { selectedIp = ip; pushNow() }
        @JavascriptInterface fun addIp(ip: String) = post {
            val t = ip.trim()
            if (t.isNotEmpty()) {
                Repo.manualPeers.value = Repo.manualPeers.value +
                    Peer(name = "manual $t", platform = "manual", ip = t, tcp = Discovery.TCP_PORT)
                Repo.setDiscovered(Discovery.snapshot())
                selectedIp = t
                pushNow()
            }
        }
        @JavascriptInterface fun pickFolder() = post { pickTree.launch(null) }
        @JavascriptInterface fun send() = post {
            val target = selectedIp
            val picks = Repo.pendingUris.value
            if (target != null && picks.isNotEmpty()) TransferService.send(this@MainActivity, target, picks)
        }
        @JavascriptInterface fun pause() = post { TransferService.pause(this@MainActivity) }
        @JavascriptInterface fun resume() = post { TransferService.resume(this@MainActivity) }
        @JavascriptInterface fun cancel() = post { TransferService.cancel(this@MainActivity) }
        @JavascriptInterface fun openReceived(at: Long) = post {
            Repo.received.value.firstOrNull { it.at == at }?.uri?.let { openFile(this@MainActivity, it) }
        }
    }

    private fun post(body: () -> Unit) = main.post(body)

    // ShareIt-style: "Share > FileShare" stages files here; the user then picks a device and hits Send.
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleShareIntent(intent)
    }

    private fun handleShareIntent(intent: Intent?) {
        val streams: List<Uri> = when (intent?.action) {
            Intent.ACTION_SEND ->
                listOfNotNull(
                    if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
                    else @Suppress("DEPRECATION") intent.getParcelableExtra(Intent.EXTRA_STREAM) as? Uri,
                )
            Intent.ACTION_SEND_MULTIPLE ->
                (if (Build.VERSION.SDK_INT >= 33) intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java)
                else @Suppress("DEPRECATION") intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM))
                    ?: emptyList()
            else -> return
        }
        if (streams.isNotEmpty()) {
            Repo.pendingUris.value = Repo.pendingUris.value + streams.map { PendingPick(it, null) }
            Toast.makeText(this, "${streams.size} file(s) ready, pick a device and send", Toast.LENGTH_SHORT).show()
        }
    }

    private companion object {
        const val REQ_FILES = 42
    }
}

private val activeStates = setOf(TState.SENDING, TState.CONNECTING, TState.RECONNECTING, TState.HASHING)

fun fmtBytes(b: Long): String {
    val v = b.toDouble()
    return when {
        v >= 1L shl 40 -> "%.2f TB".format(v / (1L shl 40))
        v >= 1L shl 30 -> "%.1f GB".format(v / (1L shl 30))
        v >= 1L shl 20 -> "%.1f MB".format(v / (1L shl 20))
        v >= 1L shl 10 -> "%.0f KB".format(v / (1L shl 10))
        else -> "$b B"
    }
}

fun openFile(ctx: Context, uri: Uri) {
    try {
        val mime = ctx.contentResolver.getType(uri) ?: "application/octet-stream"
        ctx.startActivity(
            Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, mime)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            },
        )
    } catch (e: Exception) {
        Toast.makeText(ctx, "No app can open this file", Toast.LENGTH_SHORT).show()
    }
}

// Recursively walk a SAF document tree; files come back with their folder path ("Camera/2026/x.jpg").
fun walkTree(ctx: Context, treeUri: Uri): List<PendingPick> {
    val picks = mutableListOf<PendingPick>()
    runCatching { ctx.contentResolver.takePersistableUriPermission(treeUri, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
    val resolver = ctx.contentResolver
    val rootDocId = DocumentsContract.getTreeDocumentId(treeUri)
    val rootName = runCatching {
        resolver.query(
            DocumentsContract.buildDocumentUriUsingTree(treeUri, rootDocId),
            arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME), null, null, null,
        )?.use { c -> if (c.moveToFirst()) c.getString(0) else null }
    }.getOrNull() ?: "folder"

    fun walk(docId: String, prefix: String) {
        val rows = runCatching {
            resolver.query(
                DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, docId),
                arrayOf(
                    DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                    DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                    DocumentsContract.Document.COLUMN_MIME_TYPE,
                ), null, null, null,
            )?.use { c ->
                buildList {
                    while (c.moveToNext()) add(Triple(c.getString(0), c.getString(1), c.getString(2)))
                }
            }
        }.getOrNull() ?: emptyList()
        for ((id, name, mime) in rows.sortedBy { it.second }) {
            if (mime == DocumentsContract.Document.MIME_TYPE_DIR) walk(id, "$prefix/$name")
            else picks.add(PendingPick(DocumentsContract.buildDocumentUriUsingTree(treeUri, id), prefix))
        }
    }
    walk(rootDocId, rootName)
    return picks
}

package com.fileshare.app

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.Build
import android.os.IBinder
import android.provider.OpenableColumns
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

// Foreground service so Android does not kill long (50 GB) transfers; owns discovery + receiver + send queue.
class TransferService : Service() {
    private lateinit var scope: CoroutineScope
    private var queueJob: Job? = null
    @Volatile private var paused = false
    private var lastIp: String? = null
    private lateinit var receiver: Receiver
    private var lastNotif = 0L

    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CH, "FileShare", NotificationManager.IMPORTANCE_LOW),
        )
        scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        receiver = Receiver(this) { name, uri -> Repo.addReceived(ReceivedFile(name, uri)) }
        receiver.start(scope)
        Discovery.start(this) { list -> Repo.setDiscovered(list) }
        startFg("Ready")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_SEND -> {
                val target = intent.getStringExtra(EXTRA_PEER_IP)
                val uris = intent.getStringArrayListExtra(EXTRA_URIS)?.map { Uri.parse(it) }
                val rels = intent.getStringArrayListExtra(EXTRA_RELS)
                if (target != null && uris != null) {
                    val picks = uris.mapIndexed { i, u -> PendingPick(u, rels?.getOrNull(i)?.ifEmpty { null }) }
                    enqueue(target, picks)
                }
            }
            ACTION_PAUSE -> {
                paused = true
                queueJob?.cancel()
                startFg("Paused, Resume to continue")
            }
            ACTION_RESUME -> {
                val ip = lastIp
                val items = Repo.transfers.value.filter { it.state == TState.PAUSED || it.state == TState.PENDING }
                if (ip == null || items.isEmpty()) startFg("Ready") else runQueue(items, ip)
            }
            ACTION_CANCEL -> {
                paused = false
                queueJob?.cancel()
                val active = setOf(TState.HASHING, TState.CONNECTING, TState.SENDING, TState.RECONNECTING, TState.PAUSED)
                Repo.transfers.value.forEach { if (it.state in active) it.state = TState.CANCELLED }
                Repo.tick()
                startFg("Cancelled, partials will resume")
            }
            else -> startFg("Ready")
        }
        return START_STICKY
    }

    private fun enqueue(ip: String, picks: List<PendingPick>) {
        val items = picks.mapNotNull { p ->
            runCatching {
                TransferItem(p.uri, queryName(p.uri), querySize(p.uri), p.relPath)
            }.getOrNull()
        }
        if (items.isEmpty()) return
        Repo.transfers.update { it + items }
        runQueue(items, ip)
    }

    private fun runQueue(items: List<TransferItem>, ip: String) {
        lastIp = ip
        paused = false
        queueJob?.cancel()
        queueJob = scope.launch {
            val port = Repo.peers.value.firstOrNull { it.ip == ip }?.tcp ?: Discovery.TCP_PORT
            for (item in items) {
                try {
                    Sender.send(applicationContext, item, ip, port) {
                        Repo.tick()
                        notifyProgress(item)
                    }
                } catch (e: CancellationException) {
                    // Sender already set CANCELLED; override when the pause was deliberate
                    item.state = if (paused) TState.PAUSED else TState.CANCELLED
                    Repo.tick()
                    break
                } catch (e: Exception) { /* state set by Sender */ }
            }
            startFg(if (paused) "Paused, Resume to continue" else "Idle")
        }
    }

    private fun notifyProgress(item: TransferItem) {
        val now = System.currentTimeMillis()
        if (now - lastNotif < 500) return
        lastNotif = now
        val pct = if (item.size > 0) ((item.progress * 100) / item.size).coerceIn(0, 99).toInt() else 0
        val n = NotificationCompat.Builder(this, CH)
            .setSmallIcon(android.R.drawable.ic_menu_send)
            .setContentTitle("Sending ${item.label}")
            .setContentText("$pct%, ${fmtBytes(item.progress)} of ${fmtBytes(item.size)}")
            .setProgress(100, pct, false)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .build()
        getSystemService(NotificationManager::class.java).notify(NOTIF_ID, n)
    }

    private fun startFg(text: String) {
        val n = NotificationCompat.Builder(this, CH)
            .setSmallIcon(android.R.drawable.ic_menu_send)
            .setContentTitle("FileShare")
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .build()
        ServiceCompat.startForeground(
            this, NOTIF_ID, n,
            if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC else 0,
        )
    }

    private fun queryName(uri: Uri): String {
        contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) c.getString(0)?.let { return it }
        }
        return "file"
    }

    private fun querySize(uri: Uri): Long {
        contentResolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use { c ->
            if (c.moveToFirst() && !c.isNull(0)) return c.getLong(0)
        }
        return contentResolver.openFileDescriptor(uri, "r")?.use { it.statSize } ?: 0
    }

    override fun onDestroy() {
        scope.cancel()
        receiver.stop()
        Discovery.stop()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        const val ACTION_START = "com.fileshare.app.START"
        const val ACTION_SEND = "com.fileshare.app.SEND"
        const val ACTION_CANCEL = "com.fileshare.app.CANCEL"
        const val ACTION_PAUSE = "com.fileshare.app.PAUSE"
        const val ACTION_RESUME = "com.fileshare.app.RESUME"
        const val EXTRA_PEER_IP = "peerIp"
        const val EXTRA_URIS = "uris"
        const val EXTRA_RELS = "rels"
        const val CH = "fileshare"
        const val NOTIF_ID = 1

        fun start(ctx: Context) = ContextCompat.startForegroundService(
            ctx,
            Intent(ctx, TransferService::class.java).setAction(ACTION_START),
        )

        fun send(ctx: Context, ip: String, picks: List<PendingPick>) = ContextCompat.startForegroundService(
            ctx,
            Intent(ctx, TransferService::class.java).setAction(ACTION_SEND)
                .putExtra(EXTRA_PEER_IP, ip)
                .putStringArrayListExtra(EXTRA_URIS, ArrayList(picks.map { it.uri.toString() }))
                .putStringArrayListExtra(EXTRA_RELS, ArrayList(picks.map { it.relPath ?: "" })),
        )

        fun cancel(ctx: Context) = ctx.startService(
            Intent(ctx, TransferService::class.java).setAction(ACTION_CANCEL),
        )

        fun pause(ctx: Context) = ctx.startService(
            Intent(ctx, TransferService::class.java).setAction(ACTION_PAUSE),
        )

        fun resume(ctx: Context) = ctx.startService(
            Intent(ctx, TransferService::class.java).setAction(ACTION_RESUME),
        )
    }
}

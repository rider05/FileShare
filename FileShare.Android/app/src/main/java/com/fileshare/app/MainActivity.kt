package com.fileshare.app

import android.Manifest
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ExitToApp
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Send
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.app.ActivityCompat

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= 33) {
            ActivityCompat.requestPermissions(this, arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
        TransferService.start(this)
        setContent { App() }
    }
}

private val activeStates = setOf(TState.SENDING, TState.CONNECTING, TState.RECONNECTING, TState.HASHING)

private fun TState.label(): String = when (this) {
    TState.PENDING -> "queued"
    TState.HASHING -> "hashing…"
    TState.CONNECTING -> "connecting…"
    TState.SENDING -> "sending"
    TState.RECONNECTING -> "reconnecting, will resume"
    TState.PAUSED -> "paused"
    TState.DONE -> "done"
    TState.FAILED -> "failed"
    TState.CANCELLED -> "cancelled, will resume"
}

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

@Composable
fun App() {
    MaterialTheme {
        val ctx = LocalContext.current
        val peers by Repo.peers.collectAsState()
        val transfers by Repo.transfers.collectAsState()
        val received by Repo.received.collectAsState()
        val pending by Repo.pendingUris.collectAsState()
        var selectedIp by remember { mutableStateOf<String?>(null) }
        var ip by remember { mutableStateOf("") }
        val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) {
            Repo.pendingUris.value = it
        }

        Column(
            Modifier.fillMaxSize().padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Text("FileShare", style = MaterialTheme.typography.headlineMedium)
            Text(
                if (peers.isEmpty()) "Looking for devices on this WiFi…" else "${peers.size} device(s) on this WiFi. Tap one to select.",
                style = MaterialTheme.typography.bodySmall,
            )

            LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
                items(peers) { p ->
                    Card(
                        onClick = { selectedIp = p.ip },
                        modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
                        colors = CardDefaults.cardColors(
                            containerColor = if (p.ip == selectedIp) MaterialTheme.colorScheme.primaryContainer
                            else MaterialTheme.colorScheme.surfaceVariant,
                        ),
                    ) {
                        Column(Modifier.padding(horizontal = 12.dp, vertical = 8.dp)) {
                            Text("${p.name}  (${p.platform})", style = MaterialTheme.typography.titleMedium)
                            Text(p.ip, style = MaterialTheme.typography.bodySmall)
                        }
                    }
                }
            }

            Row(verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = ip,
                    onValueChange = { ip = it },
                    Modifier.weight(1f),
                    label = { Text("Connect by IP (if discovery is blocked)") },
                    singleLine = true,
                )
                TextButton(onClick = {
                    val t = ip.trim()
                    if (t.isNotEmpty()) {
                        Repo.manualPeers.value = Repo.manualPeers.value +
                            Peer(name = "manual $t", platform = "manual", ip = t, tcp = Discovery.TCP_PORT)
                        Repo.setDiscovered(Discovery.snapshot())
                        selectedIp = t
                    }
                }) {
                    Icon(Icons.Filled.Add, contentDescription = null, Modifier.size(16.dp))
                    Spacer(Modifier.width(4.dp))
                    Text("Add")
                }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = { pick.launch(arrayOf("*/*")) }) {
                    Icon(DescriptionIcon, contentDescription = null, Modifier.size(18.dp))
                    Spacer(Modifier.width(8.dp))
                    Text("Pick files")
                }
                Button(
                    enabled = selectedIp != null && pending.isNotEmpty(),
                    onClick = {
                        val target = selectedIp
                        val uris = pending
                        if (target != null && uris.isNotEmpty()) TransferService.send(ctx, target, uris)
                    },
                ) {
                    Icon(Icons.Filled.Send, contentDescription = null, Modifier.size(18.dp))
                    Spacer(Modifier.width(8.dp))
                    Text("Send ${pending.size} file(s)")
                }
            }

            Text("Transfers", style = MaterialTheme.typography.titleMedium)
            LazyColumn(Modifier.fillMaxWidth().heightIn(max = 220.dp)) {
                items(transfers) { t ->
                    Column(Modifier.fillMaxWidth().padding(vertical = 2.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                "${t.displayName}, ${t.state.label()}",
                                Modifier.weight(1f),
                                style = MaterialTheme.typography.bodyMedium,
                            )
                            if (t.state in activeStates) {
                                TextButton(onClick = { TransferService.pause(ctx) }) {
                                    Icon(PauseIcon, contentDescription = null, Modifier.size(16.dp))
                                    Spacer(Modifier.width(4.dp))
                                    Text("Pause")
                                }
                                TextButton(onClick = { TransferService.cancel(ctx) }) {
                                    Icon(Icons.Filled.Close, contentDescription = null, Modifier.size(16.dp))
                                    Spacer(Modifier.width(4.dp))
                                    Text("Cancel")
                                }
                            } else if (t.state == TState.PAUSED || t.state == TState.PENDING) {
                                TextButton(onClick = { TransferService.resume(ctx) }) {
                                    Icon(Icons.Filled.PlayArrow, contentDescription = null, Modifier.size(16.dp))
                                    Spacer(Modifier.width(4.dp))
                                    Text("Resume")
                                }
                            }
                        }
                        LinearProgressIndicator(
                            progress = {
                                if (t.size > 0) (t.progress.toFloat() / t.size).coerceIn(0f, 1f) else 0f
                            },
                            modifier = Modifier.fillMaxWidth(),
                        )
                        Text(
                            "${fmtBytes(t.progress)} of ${fmtBytes(t.size)}" + (t.error?.let { "  ($it)" } ?: ""),
                            style = MaterialTheme.typography.bodySmall,
                        )
                    }
                }
            }

            Text("Received", style = MaterialTheme.typography.titleMedium)
            LazyColumn(Modifier.fillMaxWidth().heightIn(max = 180.dp)) {
                items(received) { f ->
                    Row(
                        Modifier.fillMaxWidth().padding(vertical = 2.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(f.name, Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
                        if (f.uri != null) {
                            TextButton(onClick = { openFile(ctx, f.uri) }) {
                                Icon(Icons.Filled.ExitToApp, contentDescription = null, Modifier.size(16.dp))
                                Spacer(Modifier.width(4.dp))
                                Text("Open")
                            }
                        }
                    }
                }
            }
        }
    }
}

package com.fileshare.app

import android.net.Uri
import kotlinx.coroutines.flow.MutableStateFlow

data class ReceivedFile(val name: String, val uri: Uri?, val at: Long = System.currentTimeMillis())

// Live incoming transfer for the UI: label, total size, bytes written so far.
data class RecvProgress(val name: String, val size: Long, val received: Long)

// A file staged for sending; relPath = folder path inside a shared folder ("Trip/day1"), null for flat files.
data class PendingPick(val uri: Uri, val relPath: String?)

object Repo {
    val peers = MutableStateFlow<List<Peer>>(emptyList())
    val manualPeers = MutableStateFlow<List<Peer>>(emptyList())
    val transfers = MutableStateFlow<List<TransferItem>>(emptyList())
    val receiving = MutableStateFlow<List<RecvProgress>>(emptyList())
    val received = MutableStateFlow<List<ReceivedFile>>(emptyList())
    val pendingUris = MutableStateFlow<List<PendingPick>>(emptyList())

    fun setDiscovered(list: List<Peer>) {
        peers.value = (manualPeers.value + list).distinctBy { it.ip }.sortedBy { it.name }
    }

    fun tick() {
        transfers.value = transfers.value.toList()
    }

    // progress updates reuse the existing row so a running transfer keeps its place in the list
    fun setRecv(name: String, size: Long, received: Long) {
        val cur = receiving.value
        val i = cur.indexOfFirst { it.name == name }
        receiving.value =
            if (i < 0) cur + RecvProgress(name, size, received)
            else cur.toMutableList().also { it[i] = RecvProgress(name, size, received) }
    }

    fun clearRecv(name: String) {
        receiving.value = receiving.value.filter { it.name != name }
    }

    fun addReceived(f: ReceivedFile) {
        received.value = listOf(f) + received.value
    }
}

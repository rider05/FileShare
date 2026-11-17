package com.fileshare.app

import android.net.Uri
import kotlinx.coroutines.flow.MutableStateFlow

data class ReceivedFile(val name: String, val uri: Uri?, val at: Long = System.currentTimeMillis())

object Repo {
    val peers = MutableStateFlow<List<Peer>>(emptyList())
    val manualPeers = MutableStateFlow<List<Peer>>(emptyList())
    val transfers = MutableStateFlow<List<TransferItem>>(emptyList())
    val received = MutableStateFlow<List<ReceivedFile>>(emptyList())
    val pendingUris = MutableStateFlow<List<Uri>>(emptyList())

    fun setDiscovered(list: List<Peer>) {
        peers.value = (manualPeers.value + list).distinctBy { it.ip }.sortedBy { it.name }
    }

    fun tick() {
        transfers.value = transfers.value.toList()
    }

    fun addReceived(f: ReceivedFile) {
        received.value = listOf(f) + received.value
    }
}

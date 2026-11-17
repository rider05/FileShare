package com.fileshare.app

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentHashMap

data class Peer(
    val name: String,
    val platform: String,
    val ip: String,
    val tcp: Int,
    val lastSeen: Long = System.currentTimeMillis(),
)

// UDP broadcast discovery. Same wire format as the Windows side (Proto.cs).
object Discovery {
    const val UDP_PORT = 53515
    const val TCP_PORT = 53516
    private const val APP_TAG = "fileshare"

    @Volatile private var socket: DatagramSocket? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var scope: CoroutineScope? = null
    private val peers = ConcurrentHashMap<String, Peer>()

    fun start(context: Context, onPeers: (List<Peer>) -> Unit) {
        if (scope != null) return
        val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        multicastLock = wifi.createMulticastLock("fs-mc").apply {
            setReferenceCounted(false)
            acquire()
        }
        wifiLock = try {
            val mode = if (Build.VERSION.SDK_INT >= 29) WifiManager.WIFI_MODE_FULL_LOW_LATENCY
            else WifiManager.WIFI_MODE_FULL_HIGH_PERF
            wifi.createWifiLock(mode, "fs-wl").apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (e: Exception) {
            null
        }

        val s = DatagramSocket(null).apply {
            reuseAddress = true
            bind(InetSocketAddress(UDP_PORT))
            broadcast = true
        }
        socket = s
        val self = deviceName()
        val myScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        scope = myScope

        myScope.launch {
            while (isActive) {
                runCatching {
                    val json = JSONObject()
                        .put("app", APP_TAG)
                        .put("name", self)
                        .put("platform", "android")
                        .put("tcp", TCP_PORT)
                    val b = json.toString().toByteArray(Charsets.UTF_8)
                    for (t in broadcastTargets()) s.send(DatagramPacket(b, b.size, t, UDP_PORT))
                }
                delay(2000)
            }
        }
        myScope.launch {
            val buf = ByteArray(4096)
            while (isActive) {
                try {
                    val p = DatagramPacket(buf, buf.size)
                    s.receive(p)
                    val o = JSONObject(String(buf, 0, p.length, Charsets.UTF_8))
                    if (o.optString("app") != APP_TAG) continue
                    val name = o.optString("name")
                    val ip = p.address.hostAddress ?: continue
                    if (name.isEmpty() || (name == self && o.optString("platform") == "android")) continue
                    peers[ip] = Peer(name, o.optString("platform"), ip, o.optInt("tcp", TCP_PORT))
                    onPeers(snapshot())
                } catch (e: Exception) {
                    // socket closed on stop(); anything else — keep listening
                }
            }
        }
    }

    fun snapshot(): List<Peer> {
        val cutoff = System.currentTimeMillis() - 6000
        peers.entries.removeIf { it.value.lastSeen < cutoff }
        return peers.values.sortedBy { it.name }
    }

    // Auto-detect every live IPv4 interface (hotspot, VPN, dock adapters) and announce on each
    // subnet's own broadcast address, plus the global one. Recomputed every tick so network
    // changes are picked up automatically.
    private fun broadcastTargets(): List<InetAddress> {
        val targets = mutableListOf(InetAddress.getByName("255.255.255.255"))
        try {
            val nis = java.net.NetworkInterface.getNetworkInterfaces() ?: return targets
            for (ni in nis.asIterator()) {
                if (!ni.isUp || ni.isLoopback || ni.isVirtual) continue
                for (ia in ni.inetAddresses.asIterator()) {
                    if (ia !is java.net.Inet4Address || ia.isLoopbackAddress) continue
                    val iface = ni.interfaceAddresses.firstOrNull { it.address == ia }
                    val prefix = (iface?.networkPrefixLength?.toInt() ?: 32).coerceIn(0, 32)
                    val mask = prefixToMask(prefix)
                    val ip = ia.address
                    val bc = ByteArray(4) { (ip[it].toInt() or mask[it].toInt().inv()).toByte() }
                    val b = InetAddress.getByAddress(bc)
                    if (targets.none { it.address.contentEquals(bc) }) targets.add(b)
                }
            }
        } catch (e: Exception) { /* fall back to the global broadcast only */ }
        return targets
    }

    private fun prefixToMask(prefix: Int): ByteArray {
        val m = if (prefix == 0) 0 else (-1 shl (32 - prefix))
        return byteArrayOf(
            ((m ushr 24) and 0xFF).toByte(),
            ((m ushr 16) and 0xFF).toByte(),
            ((m ushr 8) and 0xFF).toByte(),
            (m and 0xFF).toByte(),
        )
    }

    fun stop() {
        scope?.cancel()
        scope = null
        socket?.close()
        socket = null
        runCatching { multicastLock?.release() }
        multicastLock = null
        runCatching { wifiLock?.release() }
        wifiLock = null
    }

    fun deviceName(): String = Build.MODEL.ifEmpty { "Android" }
}

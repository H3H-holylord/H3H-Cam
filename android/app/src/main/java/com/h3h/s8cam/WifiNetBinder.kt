package com.h3h.s8cam

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.system.ErrnoException
import android.system.OsConstants
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.Socket

/**
 * Привязывает сетевые сокеты напрямую к физическому интерфейсу Wi-Fi (TRANSPORT_WIFI),
 * чтобы локальный трафик между смартфоном и ПК обходил включённый на телефоне VPN (tun0),
 * и распознаёт системную блокировку EPERM (файрвол прошивки Vivo/Xiaomi/OPPO или VPN Kill-Switch).
 */
object WifiNetBinder {

    fun findWifiNetwork(context: Context?): Network? {
        if (context == null) return null
        return try {
            val cm = context.applicationContext.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
                ?: return null
            @Suppress("DEPRECATION")
            cm.allNetworks.firstOrNull { net ->
                val caps = cm.getNetworkCapabilities(net)
                caps != null && caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
            }
        } catch (_: Exception) {
            null
        }
    }

    fun bind(context: Context?, socket: DatagramSocket): Boolean {
        val net = findWifiNetwork(context) ?: return false
        return try {
            net.bindSocket(socket)
            true
        } catch (_: Exception) {
            false
        }
    }

    fun bind(context: Context?, socket: Socket): Boolean {
        val net = findWifiNetwork(context) ?: return false
        return try {
            net.bindSocket(socket)
            true
        } catch (_: Exception) {
            false
        }
    }

    fun localWifiIpv4(context: Context?): String? {
        if (context == null) return null
        try {
            val cm = context.applicationContext.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            val net = findWifiNetwork(context)
            if (cm != null && net != null) {
                val props = cm.getLinkProperties(net)
                val addr = props?.linkAddresses?.firstOrNull { it.address is Inet4Address && !it.address.isLoopbackAddress }
                if (addr != null) return addr.address.hostAddress
            }
        } catch (_: Exception) {}
        return null
    }

    fun wifiBroadcastAddresses(context: Context?): List<InetAddress> {
        val result = LinkedHashSet<InetAddress>()
        try {
            if (context != null) {
                val cm = context.applicationContext.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
                val net = findWifiNetwork(context)
                if (cm != null && net != null) {
                    val props = cm.getLinkProperties(net)
                    props?.linkAddresses?.forEach { la ->
                        val a = la.address
                        if (a is Inet4Address && !a.isLoopbackAddress) {
                            val raw = a.address
                            val prefix = la.prefixLength.coerceIn(8, 30)
                            val mask = (-1 shl (32 - prefix))
                            val ipInt = ((raw[0].toInt() and 0xFF) shl 24) or
                                ((raw[1].toInt() and 0xFF) shl 16) or
                                ((raw[2].toInt() and 0xFF) shl 8) or
                                (raw[3].toInt() and 0xFF)
                            val bcastInt = (ipInt and mask) or mask.inv()
                            val bcastBytes = byteArrayOf(
                                ((bcastInt ushr 24) and 0xFF).toByte(),
                                ((bcastInt ushr 16) and 0xFF).toByte(),
                                ((bcastInt ushr 8) and 0xFF).toByte(),
                                (bcastInt and 0xFF).toByte()
                            )
                            result.add(InetAddress.getByAddress(bcastBytes))
                        }
                    }
                }
            }
        } catch (_: Exception) {}
        try {
            result.add(InetAddress.getByName("255.255.255.255"))
        } catch (_: Exception) {}
        return result.toList()
    }

    fun prioritizeCandidateIps(context: Context?, candidates: List<String>): List<String> {
        val unique = candidates.map { it.trim() }.filter { it.isNotEmpty() }.distinct()
        val myIp = localWifiIpv4(context) ?: return unique
        val myPrefix = myIp.substringBeforeLast('.', "")
        if (myPrefix.isEmpty()) return unique
        return unique.sortedBy { ip -> if (ip.substringBeforeLast('.', "") == myPrefix) 0 else 1 }
    }

    fun isEperm(error: Throwable?): Boolean {
        var cur = error
        var depth = 0
        while (cur != null && depth < 8) {
            if (cur is ErrnoException && cur.errno == OsConstants.EPERM) return true
            val msg = cur.message ?: ""
            if (msg.contains("EPERM", ignoreCase = true) || msg.contains("Operation not permitted", ignoreCase = true)) {
                return true
            }
            cur = cur.cause
            depth++
        }
        return false
    }

    fun formatError(context: Context, error: Throwable): String {
        if (isEperm(error)) {
            return context.getString(R.string.wifi_eperm_hint)
        }
        return error.message ?: context.getString(R.string.timeout)
    }
}

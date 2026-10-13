package com.h3h.s8cam

import java.net.URI

data class WifiPairTarget(val ip: String, val token: String) {
    companion object {
        fun parse(value: String): WifiPairTarget {
            require(value.length <= 256)
            val uri = URI(value)
            require(uri.scheme == "h3hcam" && uri.host == "wifi" && uri.userInfo == null &&
                uri.port == -1 && uri.fragment == null && uri.path.isNullOrEmpty())
            val parts = (uri.rawQuery ?: "").split('&').map { it.split('=', limit = 2) }
            require(parts.all { it.size == 2 } && parts.map { it[0] }.distinct().size == parts.size)
            val query = parts.associate { it[0] to it[1] }
            val ip = query["pc"] ?: error("Missing PC address")
            val quads = ip.split('.')
            require(quads.size == 4 && quads.all { it.matches(Regex("[0-9]{1,3}")) && it.toInt() in 0..255 })
            require(quads[0].toInt() in 1..223 && quads[0].toInt() != 127 && ip != "0.0.0.0")
            val token = query["token"] ?: error("Update the Windows client")
            require(token.matches(Regex("[a-f0-9]{32}")))
            return WifiPairTarget(ip, token)
        }
    }
}

package com.h3h.s8cam

import java.net.URI

data class WifiPairTarget(
    val ip: String,
    val token: String,
    val altIps: List<String> = emptyList(),
    val discoveryPort: Int = 5005
) {
    companion object {
        private fun isValidLanIpv4(ip: String): Boolean {
            val quads = ip.split('.')
            if (quads.size != 4 || !quads.all { it.matches(Regex("[0-9]{1,3}")) && it.toInt() in 0..255 }) return false
            val first = quads[0].toInt()
            return first in 1..223 && first != 127 && ip != "0.0.0.0"
        }

        fun parse(value: String): WifiPairTarget {
            require(value.length <= 384)
            val uri = URI(value)
            require(uri.scheme == "h3hcam" && uri.host == "wifi" && uri.userInfo == null &&
                uri.port == -1 && uri.fragment == null && uri.path.isNullOrEmpty())
            val parts = (uri.rawQuery ?: "").split('&').map { it.split('=', limit = 2) }
            require(parts.all { it.size == 2 } && parts.map { it[0] }.distinct().size == parts.size)
            val query = parts.associate { it[0] to it[1] }
            val ip = query["pc"] ?: error("Missing PC address")
            require(isValidLanIpv4(ip))
            val token = query["token"] ?: error("Update the Windows client")
            require(token.matches(Regex("[a-f0-9]{32}")))

            val altIps = query["alt"]?.split(',', ';')
                ?.map { it.trim() }
                ?.filter { it.isNotEmpty() && it != ip && isValidLanIpv4(it) }
                ?.distinct()
                ?: emptyList()

            val dp = query["dp"]?.toIntOrNull()?.takeIf { it in 1024..65535 } ?: 5005
            return WifiPairTarget(ip, token, altIps, dp)
        }
    }
}

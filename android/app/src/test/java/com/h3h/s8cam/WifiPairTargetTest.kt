package com.h3h.s8cam

import org.junit.Assert.*
import org.junit.Test

class WifiPairTargetTest {
    private val token = "0123456789abcdef0123456789abcdef"
    @Test fun parsesPcInvitation() {
        assertEquals(WifiPairTarget("192.168.1.100", token), WifiPairTarget.parse("h3hcam://wifi?pc=192.168.1.100&token=$token"))
    }
    @Test fun rejectsForeignMalformedAndOldCodes() {
        listOf("https://wifi?pc=192.168.1.100&token=$token", "h3hcam://wifi?pc=127.0.0.1&token=$token",
            "h3hcam://wifi?pc=192.168.1.999&token=$token", "h3hcam://wifi?pc=example.com&token=$token",
            "h3hcam://wifi?pc=192.168.1.100&port=5000", "h3hcam://wifi?pc=192.168.1.100&token=x",
            "h3hcam://wifi?pc=192.168.1.100&token=$token&token=$token", "h3hcam://user@wifi?pc=192.168.1.100&token=$token").forEach {
            try { WifiPairTarget.parse(it); fail("Accepted $it") } catch (_: IllegalArgumentException) { }
            catch (_: IllegalStateException) { }
        }
    }
}

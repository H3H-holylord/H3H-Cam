package com.h3h.s8cam

import android.content.Intent
import android.graphics.BitmapFactory
import android.os.SystemClock
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.zxing.BinaryBitmap
import com.google.zxing.MultiFormatReader
import com.google.zxing.RGBLuminanceSource
import com.google.zxing.common.HybridBinarizer
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class WifiPairingTest {
    @Test fun authenticatedControlSocketReplies() {
        val latch = java.util.concurrent.CountDownLatch(1)
        val token = "0123456789abcdef0123456789abcdef"
        java.net.DatagramSocket(5005, java.net.InetAddress.getByName("127.0.0.1")).use { pc ->
            pc.soTimeout = 3000
            val sender = RtpH264Sender("127.0.0.1", 5600, 20000000, "h264", {}, { true }, { latch.countDown() }, token)
            try {
                val hello = java.net.DatagramPacket(ByteArray(200), 200)
                pc.receive(hello)
                val command = org.json.JSONObject().put("command", "HEARTBEAT").put("session", token).toString().toByteArray()
                pc.send(java.net.DatagramPacket(command, command.size, hello.address, hello.port))
                assertTrue("Authenticated control command reaches service", latch.await(3, java.util.concurrent.TimeUnit.SECONDS))
            } finally { sender.close() }
        }
    }
    @Test fun windowsQrDecodesOnAndroid() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val root = context.getExternalFilesDir(null)!!
        val bmp = BitmapFactory.decodeFile(File(root, "pairing-qr.png").path)
        val pixels = IntArray(bmp.width * bmp.height); bmp.getPixels(pixels, 0, bmp.width, 0, 0, bmp.width, bmp.height)
        val decoded = MultiFormatReader().decode(BinaryBitmap(HybridBinarizer(RGBLuminanceSource(bmp.width, bmp.height, pixels)))).text
        assertEquals(File(root, "pairing-qr.txt").readText().trim(), decoded)
        assertEquals(32, WifiPairTarget.parse(decoded).token.length)
        bmp.recycle()
    }

    @Test fun livePairUsesPcSettingsAndStopsWithPc() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val target = WifiPairTarget.parse(File(context.getExternalFilesDir(null), "live-qr.txt").readText().trim())
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            val settings = WifiPairClient.connect(context, target)
            assertEquals(1920, settings.width); assertEquals(1080, settings.height); assertEquals(32000000, settings.bitrate)
            assertEquals(target.token, settings.sessionId)
            scenario.onActivity { it.startForegroundService(settings.applyTo(Intent(it, StreamService::class.java))) }
            val startedDeadline = SystemClock.elapsedRealtime() + 15000
            while (StreamStats.frames.get() < 60 && SystemClock.elapsedRealtime() < startedDeadline) SystemClock.sleep(100)
            assertTrue("Camera encodes after native pairing", StreamStats.frames.get() >= 60)
            val stoppedDeadline = SystemClock.elapsedRealtime() + 20000
            while (StreamStats.running && SystemClock.elapsedRealtime() < stoppedDeadline) SystemClock.sleep(100)
            assertFalse("PC STOP shuts down phone camera", StreamStats.running)
            assertEquals("PC bitrate change reaches phone", 24000000, StreamStats.targetBitrate)
        }
    }
}

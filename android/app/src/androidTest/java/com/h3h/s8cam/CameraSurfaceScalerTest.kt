package com.h3h.s8cam

import android.graphics.Color
import android.graphics.Paint
import android.graphics.SurfaceTexture
import android.hardware.camera2.*
import android.content.Context
import android.os.Handler
import android.os.HandlerThread
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.Assume.assumeTrue
import org.junit.runner.RunWith
import java.io.File
import java.io.FileOutputStream
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/** Encode asymmetric 4K quadrants through the real GPU path, twice to check cleanup.
 * Pull qhd-scaler-*.h264 from targetContext.getExternalFilesDir(null) and check
 * decoded 2560x1440 dimensions and red/green/blue/white corner orientation.
 */
@RunWith(AndroidJUnit4::class)
class CameraSurfaceScalerTest {
    @Test fun capturesCameraThroughGpuEncoder() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val manager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
        val cameraId = manager.cameraIdList.first()
        val source = manager.getCameraCharacteristics(cameraId).get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
            ?.getOutputSizes(SurfaceTexture::class.java)?.filter {
                it.width >= 1920 && it.height >= 1080 && it.width.toLong()*9 == it.height.toLong()*16
            }?.maxByOrNull { it.width.toLong()*it.height }
        assumeTrue("Camera2 must expose Full HD or larger SurfaceTexture", source != null)
        val width = if (source!!.width >= 2560) 2560 else 1920
        val height = width*9/16
        assumeTrue("Device must encode output", H264Encoder.supports(width, height, 30))
        println("GPU camera test: ${source.width}x${source.height} -> ${width}x${height}")
        val thread = HandlerThread("QhdCameraTest").apply { start() }
        val handler = Handler(thread.looper)
        val errors = ConcurrentLinkedQueue<String>()
        val frames = CountDownLatch(60)
        val ready = CountDownLatch(1)
        var camera: CameraDevice? = null
        var session: CameraCaptureSession? = null
        val scaler = CameraSurfaceScaler(handler) { errors.add(it) }
        val output = File(context.getExternalFilesDir(null), "qhd-scaler-camera.h264")
        FileOutputStream(output).use { stream ->
            val encoder = H264Encoder(width, height, 30, 12_000_000,
                onError = { errors.add(it) }, onEncodedData = { data, config, _, _ ->
                    synchronized(stream) { stream.write(byteArrayOf(0, 0, 0, 1)); stream.write(data) }
                    if (!config) frames.countDown()
                })
            try {
                encoder.start()
                onHandler(handler) { scaler.start(encoder.inputSurface, source.width, source.height, width, height) }
                manager.openCamera(cameraId, object : CameraDevice.StateCallback() {
                    override fun onOpened(device: CameraDevice) {
                        camera = device
                        device.createCaptureSession(listOf(scaler.cameraSurface), object : CameraCaptureSession.StateCallback() {
                            override fun onConfigured(active: CameraCaptureSession) {
                                session = active
                                try {
                                    val request = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD)
                                    request.addTarget(scaler.cameraSurface)
                                    val ranges = manager.getCameraCharacteristics(cameraId)
                                        .get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES).orEmpty()
                                    ranges.filter { it.upper == 30 }.minByOrNull { it.upper-it.lower }?.let {
                                        request.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it)
                                    }
                                    active.setRepeatingRequest(request.build(), null, handler)
                                } catch (e: Exception) { errors.add(e.toString()) }
                                ready.countDown()
                            }
                            override fun onConfigureFailed(active: CameraCaptureSession) { errors.add("Camera session failed"); ready.countDown() }
                        }, handler)
                    }
                    override fun onDisconnected(device: CameraDevice) { device.close(); errors.add("Camera disconnected"); ready.countDown() }
                    override fun onError(device: CameraDevice, error: Int) { device.close(); errors.add("Camera error $error"); ready.countDown() }
                }, handler)
                assertTrue("Camera session ready", ready.await(10, TimeUnit.SECONDS))
                assertTrue("No startup errors: $errors", errors.isEmpty())
                assertTrue("Real Camera2 frames through GPU", frames.await(12, TimeUnit.SECONDS))
                assertTrue("No GPU/codec errors: $errors", errors.isEmpty())
            } finally {
                try { onHandler(handler) { session?.close(); camera?.close(); scaler.close() } }
                finally { encoder.stop(); thread.quitSafely(); thread.join(3000) }
            }
        }
        assertTrue("Encoded camera artifact", output.length() > 100000)
    }

    @Test fun scales4kToQhdAndRestarts() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        assertTrue("Device must encode QHD", H264Encoder.supports(2560, 1440, 30))
        repeat(2) { round ->
            val errors = ConcurrentLinkedQueue<String>()
            val frames = AtomicInteger()
            val thread = HandlerThread("QhdScalerTest").apply { start() }
            val handler = Handler(thread.looper)
            val scaler = CameraSurfaceScaler(handler) { errors.add(it) }
            val output = File(context.getExternalFilesDir(null), "qhd-scaler-$round.h264")
            FileOutputStream(output).use { stream ->
                val encoder = H264Encoder(2560, 1440, 30, 12_000_000,
                    onError = { errors.add(it) },
                    onEncodedData = { data, config, _, _ ->
                        synchronized(stream) { stream.write(byteArrayOf(0, 0, 0, 1)); stream.write(data) }
                        if (!config) frames.incrementAndGet()
                    })
                try {
                    encoder.start()
                    onHandler(handler) { scaler.start(encoder.inputSurface, 3840, 2160, 2560, 1440) }
                    val surface = scaler.cameraSurface
                    val paint = Paint()
                    repeat(40) {
                        val canvas = surface.lockCanvas(null)
                        try {
                            val w = canvas.width.toFloat(); val h = canvas.height.toFloat()
                            assertEquals(3840, canvas.width); assertEquals(2160, canvas.height)
                            paint.color = Color.RED; canvas.drawRect(0f, 0f, w/2, h/2, paint)
                            paint.color = Color.GREEN; canvas.drawRect(w/2, 0f, w, h/2, paint)
                            paint.color = Color.BLUE; canvas.drawRect(0f, h/2, w/2, h, paint)
                            paint.color = Color.WHITE; canvas.drawRect(w/2, h/2, w, h, paint)
                        } finally { surface.unlockCanvasAndPost(canvas) }
                        Thread.sleep(34)
                    }
                    Thread.sleep(250)
                    assertTrue("No GPU/codec errors: $errors", errors.isEmpty())
                    assertTrue("Encoded frames", frames.get() >= 20)
                } finally {
                    try { onHandler(handler) { scaler.close() } } finally {
                        encoder.stop(); thread.quitSafely(); thread.join(3000)
                    }
                }
            }
            assertTrue("Encoded artifact", output.length() > 10000)
        }
    }

    private fun onHandler(handler: Handler, action: () -> Unit) {
        val done = CountDownLatch(1)
        var error: Throwable? = null
        handler.post { try { action() } catch (e: Throwable) { error = e } finally { done.countDown() } }
        assertTrue("GPU handler completed", done.await(10, TimeUnit.SECONDS))
        error?.let { throw it }
    }
}

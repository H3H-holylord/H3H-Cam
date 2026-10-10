package com.h3h.s8cam

import android.graphics.SurfaceTexture
import android.opengl.EGL14
import android.opengl.EGLExt
import android.opengl.GLES11Ext
import android.opengl.GLES20
import android.os.Handler
import android.os.Looper
import android.view.Surface
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicBoolean

/** GPU-only camera SurfaceTexture -> encoder Surface. No CPU readback or extra decoder. */
internal class CameraSurfaceScaler(private val handler: Handler, private val onError: (String) -> Unit) {
    private var display = EGL14.EGL_NO_DISPLAY
    private var context = EGL14.EGL_NO_CONTEXT
    private var window = EGL14.EGL_NO_SURFACE
    private var texture: SurfaceTexture? = null
    private var input: Surface? = null
    val cameraSurface: Surface get() = input ?: error("Поверхность масштабирования не создана")
    private var textureId = 0
    private var program = 0
    private var outputWidth = 0
    private var outputHeight = 0
    private var positionLocation = -1
    private var uvLocation = -1
    private var transformLocation = -1
    private var samplerLocation = -1
    private val matrix = FloatArray(16)
    private val pending = AtomicBoolean()
    private var previousPts = 0L
    @Volatile private var closed = false
    private var failed = false
    private val vertices = ByteBuffer.allocateDirect(16 * 4).order(ByteOrder.nativeOrder()).asFloatBuffer().apply {
        put(floatArrayOf(-1f,-1f,0f,0f, 1f,-1f,1f,0f, -1f,1f,0f,1f, 1f,1f,1f,1f)); position(0)
    }

    fun start(encoderSurface: Surface, captureWidth: Int, captureHeight: Int, width: Int, height: Int) {
        check(Looper.myLooper() == handler.looper)
        try {
            outputWidth = width; outputHeight = height
            display = EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY)
            check(display != EGL14.EGL_NO_DISPLAY) { "EGL display" }
            val version = IntArray(2)
            check(EGL14.eglInitialize(display, version, 0, version, 1)) { "EGL init" }
            val configs = arrayOfNulls<android.opengl.EGLConfig>(1)
            val count = IntArray(1)
            val attributes = intArrayOf(EGL14.EGL_RED_SIZE,8, EGL14.EGL_GREEN_SIZE,8, EGL14.EGL_BLUE_SIZE,8,
                EGL14.EGL_ALPHA_SIZE,8, EGL14.EGL_RENDERABLE_TYPE,EGL14.EGL_OPENGL_ES2_BIT,
                EGL14.EGL_SURFACE_TYPE,EGL14.EGL_WINDOW_BIT, 0x3142,1, EGL14.EGL_NONE)
            check(EGL14.eglChooseConfig(display, attributes, 0, configs, 0, 1, count, 0) && count[0] > 0) { "EGL config" }
            context = EGL14.eglCreateContext(display, configs[0], EGL14.EGL_NO_CONTEXT,
                intArrayOf(EGL14.EGL_CONTEXT_CLIENT_VERSION,2,EGL14.EGL_NONE), 0)
            check(context != EGL14.EGL_NO_CONTEXT) { "EGL context" }
            window = EGL14.eglCreateWindowSurface(display, configs[0], encoderSurface, intArrayOf(EGL14.EGL_NONE), 0)
            check(window != EGL14.EGL_NO_SURFACE) { "EGL encoder surface" }
            check(EGL14.eglMakeCurrent(display, window, window, context)) { "EGL current" }
            EGL14.eglSwapInterval(display, 0)
            program = makeProgram()
            positionLocation = GLES20.glGetAttribLocation(program, "position")
            uvLocation = GLES20.glGetAttribLocation(program, "uv")
            transformLocation = GLES20.glGetUniformLocation(program, "transform")
            samplerLocation = GLES20.glGetUniformLocation(program, "camera")
            val ids = IntArray(1)
            GLES20.glGenTextures(1, ids, 0); textureId = ids[0]
            GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, textureId)
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_LINEAR)
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR)
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_S, GLES20.GL_CLAMP_TO_EDGE)
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_T, GLES20.GL_CLAMP_TO_EDGE)
            texture = SurfaceTexture(textureId).also { t ->
                t.setDefaultBufferSize(captureWidth, captureHeight)
                t.setOnFrameAvailableListener({
                    // Only one draw can wait. updateTexImage acquires the latest camera frame.
                    if (!closed && !failed && pending.compareAndSet(false, true)) {
                        handler.post { pending.set(false); drawLatest() }
                    }
                }, handler)
                input = Surface(t)
            }
        } catch (e: Exception) {
            close()
            throw IllegalStateException("GPU масштабирование камеры: ${e.message}", e)
        }
    }

    private fun drawLatest() {
        if (closed || failed) return
        try {
            check(EGL14.eglMakeCurrent(display, window, window, context)) { "EGL current" }
            val t = texture ?: return
            t.updateTexImage(); t.getTransformMatrix(matrix)
            GLES20.glViewport(0, 0, outputWidth, outputHeight)
            GLES20.glUseProgram(program)
            GLES20.glActiveTexture(GLES20.GL_TEXTURE0)
            GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, textureId)
            GLES20.glUniform1i(samplerLocation, 0)
            // SurfaceTexture owns the camera transform; no second rotation or mirror.
            GLES20.glUniformMatrix4fv(transformLocation, 1, false, matrix, 0)
            vertices.position(0); GLES20.glVertexAttribPointer(positionLocation, 2, GLES20.GL_FLOAT, false, 16, vertices)
            vertices.position(2); GLES20.glVertexAttribPointer(uvLocation, 2, GLES20.GL_FLOAT, false, 16, vertices)
            GLES20.glEnableVertexAttribArray(positionLocation); GLES20.glEnableVertexAttribArray(uvLocation)
            GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4)
            val pts = CaptureTimestamp.next(t.timestamp, previousPts)
            previousPts = pts
            check(EGLExt.eglPresentationTimeANDROID(display, window, pts)) { "EGL timestamp" }
            check(EGL14.eglSwapBuffers(display, window)) { "EGL swap" }
        } catch (e: Exception) {
            if (!closed) { failed = true; onError("GPU масштабирование: ${e.message}") }
        }
    }

    private fun makeProgram(): Int {
        val vertex = shader(GLES20.GL_VERTEX_SHADER, """
            attribute vec2 position;
            attribute vec2 uv;
            uniform mat4 transform;
            varying mediump vec2 cameraUv;
            void main() { gl_Position=vec4(position,0.0,1.0); cameraUv=(transform*vec4(uv,0.0,1.0)).xy; }
        """.trimIndent())
        var fragment = 0
        var result = 0
        try {
            fragment = shader(GLES20.GL_FRAGMENT_SHADER, """
                #extension GL_OES_EGL_image_external : require
                precision mediump float;
                uniform samplerExternalOES camera;
                varying mediump vec2 cameraUv;
                void main() { gl_FragColor=texture2D(camera,cameraUv); }
            """.trimIndent())
            result = GLES20.glCreateProgram()
            GLES20.glAttachShader(result, vertex); GLES20.glAttachShader(result, fragment)
            GLES20.glLinkProgram(result)
            val linked = IntArray(1)
            GLES20.glGetProgramiv(result, GLES20.GL_LINK_STATUS, linked, 0)
            check(linked[0] != 0) { GLES20.glGetProgramInfoLog(result) }
            return result
        } catch (e: Exception) { if (result != 0) GLES20.glDeleteProgram(result); throw e }
        finally { GLES20.glDeleteShader(vertex); if (fragment != 0) GLES20.glDeleteShader(fragment) }
    }

    private fun shader(type: Int, source: String): Int {
        val id = GLES20.glCreateShader(type)
        try {
            GLES20.glShaderSource(id, source); GLES20.glCompileShader(id)
            val compiled = IntArray(1)
            GLES20.glGetShaderiv(id, GLES20.GL_COMPILE_STATUS, compiled, 0)
            check(compiled[0] != 0) { GLES20.glGetShaderInfoLog(id) }
            return id
        } catch (e: Exception) { GLES20.glDeleteShader(id); throw e }
    }

    fun close() {
        closed = true
        texture?.setOnFrameAvailableListener(null)
        input?.release(); input = null
        texture?.release(); texture = null
        if (display != EGL14.EGL_NO_DISPLAY) {
            if (context != EGL14.EGL_NO_CONTEXT && window != EGL14.EGL_NO_SURFACE &&
                EGL14.eglMakeCurrent(display, window, window, context)) {
                if (program != 0) GLES20.glDeleteProgram(program)
                if (textureId != 0) GLES20.glDeleteTextures(1, intArrayOf(textureId), 0)
            }
            EGL14.eglMakeCurrent(display, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_CONTEXT)
            if (window != EGL14.EGL_NO_SURFACE) EGL14.eglDestroySurface(display, window)
            if (context != EGL14.EGL_NO_CONTEXT) EGL14.eglDestroyContext(display, context)
            EGL14.eglTerminate(display); EGL14.eglReleaseThread()
        }
        display = EGL14.EGL_NO_DISPLAY; context = EGL14.EGL_NO_CONTEXT; window = EGL14.EGL_NO_SURFACE
        program = 0; textureId = 0
    }
}

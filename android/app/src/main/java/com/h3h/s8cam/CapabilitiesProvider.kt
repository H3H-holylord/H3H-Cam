package com.h3h.s8cam

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject

class CapabilitiesProvider : ContentProvider() {
    override fun onCreate() = true

    override fun call(method: String, arg: String?, extras: Bundle?): Bundle {
        if (method != "capabilities") return Bundle().apply { putString("error", "unknown method") }
        val json = buildJson(requireNotNull(context))
        return Bundle().apply {
            putString("data", Base64.encodeToString(json.toByteArray(Charsets.UTF_8), Base64.NO_WRAP))
        }
    }

    companion object {
      fun buildJson(context: android.content.Context): String {
        val cameras = CameraCatalog.list(context)
        return JSONObject()
            .put("version", 1)
            .put("model", Build.MODEL)
            .put("manufacturer", Build.MANUFACTURER)
            .put("sdk", Build.VERSION.SDK_INT)
            .put("cameras", JSONArray().apply {
                cameras.forEach { camera ->
                    put(JSONObject()
                        .put("key", camera.key)
                        .put("label", camera.label)
                        .put("facing", camera.facing)
                        .put("minimumFocusDistance", camera.minimumFocusDistance)
                        .put("flash", camera.flash)
                        .put("modes", JSONArray().apply {
                            camera.modes.forEach { mode ->
                                put(JSONObject().put("width", mode.width).put("height", mode.height)
                                    .put("fps", JSONArray(mode.fps)))
                            }
                        }))
                }
            }).toString()
      }
    }

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?,
        selectionArgs: Array<out String>?, sortOrder: String?): Cursor? = null
    override fun getType(uri: Uri): String = "application/json"
    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = 0
    override fun update(uri: Uri, values: ContentValues?, selection: String?,
        selectionArgs: Array<out String>?): Int = 0
}

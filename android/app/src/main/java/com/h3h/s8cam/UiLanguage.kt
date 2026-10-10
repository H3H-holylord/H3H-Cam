package com.h3h.s8cam

import android.content.Context
import android.content.res.Configuration
import android.content.res.Resources
import java.util.Locale

/** UI preference is independent from camera settings and wire protocol values. */
object UiLanguage {
    fun selection(context: Context) = context.getSharedPreferences("s8cam_ui", 0)
        .getString("language", "auto").let { if (it in listOf("ru", "en")) it!! else "auto" }
    fun resolve(value: String, systemLanguage: String) = if (value in listOf("ru", "en")) value
        else if (systemLanguage == "ru") "ru" else "en"
    fun save(context: Context, value: String) {
        require(value in listOf("auto", "ru", "en"))
        context.getSharedPreferences("s8cam_ui", 0).edit().putString("language", value).apply()
    }
    fun context(base: Context): Context {
        val system = Resources.getSystem().configuration.locales[0].language
        val language = resolve(selection(base), system)
        val config = Configuration(base.resources.configuration)
        config.setLocale(Locale.forLanguageTag(language))
        return base.createConfigurationContext(config)
    }
    fun state(context: Context, value: String): String {
        val c = UiLanguage.context(context)
        return when (value) {
            "Остановлено" -> c.getString(R.string.state_stopped)
            "Запуск" -> c.getString(R.string.state_starting)
            "Неверные настройки" -> c.getString(R.string.state_invalid_settings)
            "Передача" -> c.getString(R.string.state_streaming)
            "Камера запущена" -> c.getString(R.string.state_camera_started)
            "USB Direct отключён" -> c.getString(R.string.state_usb_disconnected)
            "USB Direct · камера переконфигурирована" -> c.getString(R.string.state_usb_reconfigured)
            else -> if (value.startsWith("Переподключение:")) c.getString(R.string.state_reconnecting) + value.substringAfter(':') else value
        }
    }
    /** Translate display text only; camera IDs and capability JSON stay unchanged. */
    fun camera(context: Context, value: String): String {
        if (context.resources.configuration.locales[0].language != "en") return value
        return value.replace("Фронтальная", "Front").replace("Задняя", "Rear")
            .replace("Внешняя", "External").replace("логический", "logical")
            .replace("модуль", "module").replace(" мм", " mm")
    }
}

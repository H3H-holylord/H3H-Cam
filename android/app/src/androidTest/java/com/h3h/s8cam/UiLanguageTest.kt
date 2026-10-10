package com.h3h.s8cam

import android.widget.Spinner
import android.os.SystemClock
import android.graphics.Bitmap
import android.graphics.Canvas
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.Assume.assumeTrue
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class UiLanguageTest {
    @Test fun pickerRecreatesUiWithoutChangingCaptureSettings() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        assumeTrue("UI test requires an awake, unlocked phone",
            (context.getSystemService(android.content.Context.POWER_SERVICE) as android.os.PowerManager).isInteractive)
        val previous = UiLanguage.selection(context)
        val capture = StreamSettings.load(context)
        try {
            UiLanguage.save(context, "ru")
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                for ((index, language, action) in listOf(Triple(2, "en", "📡 FIND COMPUTER (WI-FI)"),
                    Triple(1, "ru", "📡 НАЙТИ КОМПЬЮТЕР (WI-FI)"), Triple(2, "en", "📡 FIND COMPUTER (WI-FI)"))) {
                    scenario.onActivity { it.findViewById<Spinner>(R.id.language_picker).setSelection(index) }
                    // Activity recreation includes asynchronous lifecycle transactions from AMS.
                    val deadline = SystemClock.elapsedRealtime() + 8000
                    var ready = false
                    while (!ready && SystemClock.elapsedRealtime() < deadline) {
                        instrumentation.waitForIdleSync()
                        scenario.onActivity { ready = it.resources.configuration.locales[0].language == language &&
                            UiLanguage.selection(it) == language && it.window.decorView.width > 0 }
                        if (!ready) SystemClock.sleep(50)
                    }
                    assertTrue("Language recreation completes: $language", ready)
                    scenario.onActivity {
                        assertEquals(language, it.resources.configuration.locales[0].language)
                        assertEquals(action, it.getString(R.string.find_computer))
                        assertEquals(language, UiLanguage.selection(it))
                        assertEquals(capture, StreamSettings.load(it))
                        assertEquals(if (language == "en") "Stopped" else "Остановлено", UiLanguage.state(it, "Остановлено"))
                    }
                    if (language == "en") {
                        scenario.onActivity { activity ->
                            // Render only this app; system dialogs/notifications do not enter the artifact.
                            val view = activity.window.decorView
                            val screenshot = Bitmap.createBitmap(view.width, view.height, Bitmap.Config.ARGB_8888)
                            view.draw(Canvas(screenshot))
                            java.io.File(context.getExternalFilesDir(null), "language-english.png").outputStream().use {
                                screenshot.compress(Bitmap.CompressFormat.PNG, 100, it)
                            }
                            screenshot.recycle()
                        }
                    }
                }
            }
            UiLanguage.save(context, "auto")
            assertEquals("auto", UiLanguage.selection(context))
            assertEquals(capture, StreamSettings.load(context))
        } finally { UiLanguage.save(context, previous) }
    }
}

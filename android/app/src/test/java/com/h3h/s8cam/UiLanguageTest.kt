package com.h3h.s8cam

import org.junit.Assert.assertEquals
import org.junit.Test

class UiLanguageTest {
    @Test fun resolvesSupportedLanguagesAndFallback() {
        assertEquals("ru", UiLanguage.resolve("auto", "ru"))
        assertEquals("en", UiLanguage.resolve("auto", "de"))
        assertEquals("en", UiLanguage.resolve("en", "ru"))
        assertEquals("ru", UiLanguage.resolve("ru", "en"))
        assertEquals("en", UiLanguage.resolve("invalid", "fr"))
    }
}

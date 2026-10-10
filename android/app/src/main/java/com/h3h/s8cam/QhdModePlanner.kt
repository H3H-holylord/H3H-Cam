package com.h3h.s8cam

/** A QHD output must have a real camera source; never upscale 1080p or invent 60 FPS. */
internal object QhdModePlanner {
    fun add(native: List<VideoMode>, capture: List<VideoMode>, encoderSupports: (Int) -> Boolean): List<VideoMode> {
        if (native.any { it.width == 2560 && it.height == 1440 }) return native
        val candidates = capture.filter {
            !it.scaled && it.width >= 2560 && it.height >= 1440 &&
                it.width.toLong() * 1440 == it.height.toLong() * 2560
        }.mapNotNull { source ->
            val fps = source.fps.filter { it in 10..60 && encoderSupports(it) }.distinct().sorted()
            if (fps.isEmpty()) null else VideoMode(2560, 1440, fps,
                captureWidth = source.width, captureHeight = source.height)
        }
        val selected = candidates.sortedWith(compareByDescending<VideoMode> { it.fps.maxOrNull() ?: 0 }
            .thenBy { it.captureWidth.toLong() * it.captureHeight }).firstOrNull() ?: return native
        return native + selected
    }
}

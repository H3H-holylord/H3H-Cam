package com.h3h.s8cam

/** Keep sensor timing; do not invent a requested-FPS clock or accumulate future PTS. */
internal object CaptureTimestamp {
    fun next(sensorNs: Long, previousNs: Long, clockNs: () -> Long = System::nanoTime): Long {
        if (previousNs == 0L) return if (sensorNs > 0L) sensorNs else clockNs()
        return if (sensorNs > previousNs) sensorNs else previousNs + 1_000L
    }
}

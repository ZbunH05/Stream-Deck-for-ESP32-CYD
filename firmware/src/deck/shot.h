#pragma once

#if defined(CYD_PERF) && CYD_PERF && defined(CYD_PERF_DEMO) && CYD_PERF_DEMO == 3
void shot_loop();
#else
inline void shot_loop() {}
#endif

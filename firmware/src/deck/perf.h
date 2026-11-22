#pragma once

#if defined(CYD_PERF) && CYD_PERF && defined(CYD_PERF_DEMO) && CYD_PERF_DEMO == 4
void perf_loop();
#else
inline void perf_loop() {}
#endif

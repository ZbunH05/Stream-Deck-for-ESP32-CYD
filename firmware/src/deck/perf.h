// Đo hiệu năng hiệu ứng (perf.cpp). Chỉ có trong bản build -DCYD_PERF=1 -DCYD_PERF_DEMO=4.
#pragma once

#if defined(CYD_PERF) && CYD_PERF && defined(CYD_PERF_DEMO) && CYD_PERF_DEMO == 4
void perf_loop();
#else
inline void perf_loop() {}
#endif

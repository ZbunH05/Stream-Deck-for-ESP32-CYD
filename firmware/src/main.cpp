#include <Arduino.h>

#include "app/motion.h"
#include "cyd/cyd_hw.h"
#include "deck/deck.h"
#include "deck/link.h"
#include "deck/perf.h"
#include "deck/sample.h"
#include "deck/shot.h"
#include "deck/store.h"

void setup() {
  cyd_init();
  motion_init();
  deck_init();
  store_init();
  link_init();
  deck_go(0, false);
  Serial.println("[APP] San sang");
}

void loop() {
  cyd_loop();
  motion_tick();
  link_poll();
  deck_tick();
  shot_loop();
  perf_loop();
}

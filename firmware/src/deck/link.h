#pragma once

#include <stdint.h>

void link_init();
void link_poll();
void link_key_down(int page, int key);
void link_key_up(int page, int key, bool cancelled);
void link_page(int page);

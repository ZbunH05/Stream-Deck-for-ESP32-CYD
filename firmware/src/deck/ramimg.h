#pragma once

#include <lvgl.h>
#include <stddef.h>
#include <stdint.h>

constexpr int RAMIMG_SLOTS = 8;

int rle_decode(const uint8_t *in, size_t in_len, uint8_t *out, size_t out_max);

bool ramimg_write(uint8_t page, uint8_t key, uint32_t offset, const uint8_t *data, size_t length, bool *complete);
const lv_image_dsc_t *ramimg_find(uint8_t page, uint8_t key);
void ramimg_clear(uint8_t page, uint8_t key);
bool ramimg_take_evicted(uint8_t *page, uint8_t *key);
void ramimg_clear_all();

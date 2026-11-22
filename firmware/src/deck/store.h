#pragma once

#include <lvgl.h>
#include <stddef.h>
#include <stdint.h>

constexpr uint32_t STORE_IMG_BYTES = 72 * 72 * 2;

bool store_init();
int store_slots();
int store_used();

const lv_image_dsc_t *store_find(uint32_t hash);

int store_begin(uint32_t hash, const uint32_t *keep, int keep_count);
bool store_write(uint32_t hash, uint32_t offset, const uint8_t *data, size_t length);
bool store_finish(uint32_t hash);

bool store_save_layout(const uint8_t *data, size_t length);
const uint8_t *store_layout(size_t *length);

uint32_t store_crc32(uint32_t crc, const uint8_t *data, size_t length);

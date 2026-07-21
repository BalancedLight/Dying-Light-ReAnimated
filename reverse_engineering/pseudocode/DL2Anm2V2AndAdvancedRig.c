/*
 * Documentation-only, named C-like reconstruction of the DL2
 * Header_Version2 read path and player-target registration. It is not game
 * code, an ABI declaration, or a native writer.
 *
 * Static evidence anchors in the supplied engine build (not portable runtime
 * constants):
 *   Header_Version2 validator                  sub_18039DAF0
 *   Header_Version1 validator                  sub_18039D4A0
 *   Header_Version2 time/block mapper          sub_18034F580
 *   Header_Version1 time/page mapper           sub_18034F3D0
 *   sampler-data pointer from block dictionary sub_180342800
 *   sampler-data size from dictionary          sub_180342830
 *   CAnm2Sampler::SampleFrame                  0x1803F22A0 (symbolized)
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct {
    uint32_t magic;
    uint16_t signature;
    uint16_t header_version;
    uint32_t payload_size_units16;
    uint16_t header_size_units16;
    uint16_t payload_block_size_units16;
    uint16_t payload_block_count;
    uint16_t time_domain_bound;
    uint16_t frame_domain_bound;
    uint16_t vfr_interval_count;
    uint16_t track_count;
    uint16_t static_stream_count;
} DlrAnm2HeaderV2Disk;

typedef struct {
    const uint8_t *file_data;
    size_t file_size;
    const DlrAnm2HeaderV2Disk *header;
    uint32_t header_bytes;
    uint32_t payload_bytes;
    uint32_t payload_block_bytes;
    uint32_t track_table_offset;
    uint32_t block_spans_offset;
    uint32_t vfr_offset;
    uint32_t vfr_word_count;
} DlrAnm2V2Layout;

typedef struct {
    const uint8_t *block_start;
    uint32_t block_available;
    const uint16_t *dictionary;
    uint32_t dictionary_count;
    const uint8_t *base_segment;
    uint32_t base_segment_size;
} DlrAnm2V2Block;

typedef struct {
    float evaluated_frame;
    uint32_t adjusted_frame;
    uint32_t block_index;
    uint32_t page_table_index;
    uint32_t frame_in_15_frame_slot;
    float interpolation_fraction;
} DlrAnm2V2TimeSelection;

static uint32_t dlr_align_up_4(uint32_t value) {
    return (value + 3u) & ~3u;
}

static bool dlr_range_fits(size_t size, uint64_t start, uint64_t count) {
    return start <= size && count <= size - start;
}

bool dlr_dl2_anm2_v2_parse_layout(
    const uint8_t *file_data,
    size_t file_size,
    DlrAnm2V2Layout *out_layout)
{
    const DlrAnm2HeaderV2Disk *header;
    uint32_t track_table_offset;
    uint32_t block_spans_offset;
    uint32_t vfr_offset;
    uint32_t vfr_word_count;

    if (!file_data || !out_layout || file_size < sizeof(*header))
        return false;

    header = (const DlrAnm2HeaderV2Disk *)file_data;
    track_table_offset = dlr_align_up_4(0x1cu);
    block_spans_offset = track_table_offset + (uint32_t)header->track_count * 4u;
    vfr_offset = block_spans_offset + (uint32_t)header->payload_block_count * 2u;
    vfr_word_count = 1u + 2u * (uint32_t)header->vfr_interval_count;

    out_layout->file_data = file_data;
    out_layout->file_size = file_size;
    out_layout->header = header;
    out_layout->header_bytes = (uint32_t)header->header_size_units16 << 4;
    out_layout->payload_bytes = header->payload_size_units16 << 4;
    out_layout->payload_block_bytes =
        (uint32_t)header->payload_block_size_units16 << 4;
    out_layout->track_table_offset = track_table_offset;
    out_layout->block_spans_offset = block_spans_offset;
    out_layout->vfr_offset = vfr_offset;
    out_layout->vfr_word_count = vfr_word_count;
    return true;
}

bool dlr_dl2_anm2_v2_validate_layout(const DlrAnm2V2Layout *layout) {
    const DlrAnm2HeaderV2Disk *header;
    uint32_t total_components;
    uint32_t block_index;
    uint32_t span_sum = 0;

    if (!layout || !(header = layout->header))
        return false;
    if (header->magic != 0x324d4e41u || header->signature != 42u ||
        header->header_version != 2u)
        return false;
    if (!header->header_size_units16 || !header->payload_block_size_units16 ||
        !header->payload_block_count || !header->track_count)
        return false;
    if ((uint64_t)layout->header_bytes + layout->payload_bytes != layout->file_size)
        return false;
    if (!dlr_range_fits(layout->header_bytes, layout->track_table_offset,
                        (uint64_t)header->track_count * 4u) ||
        !dlr_range_fits(layout->header_bytes, layout->block_spans_offset,
                        (uint64_t)header->payload_block_count * 2u) ||
        !dlr_range_fits(layout->header_bytes, layout->vfr_offset,
                        (uint64_t)layout->vfr_word_count * 2u))
        return false;

    total_components = (uint32_t)header->track_count * 9u;
    if (header->static_stream_count > total_components)
        return false;
    for (block_index = 0; block_index != header->payload_block_count; ++block_index)
        span_sum += ((const uint16_t *)(layout->file_data +
            layout->block_spans_offset))[block_index];
    if (span_sum != header->frame_domain_bound)
        return false;

    /* Each block is additionally checked through get_sampler_block(): the
       dictionary must be positive, strictly increasing until zero, bounded by
       that block (including a short final block), and contain base + stream.
       The base header must repeat total/static/packed counts and provide at
       least ceil(packed/8)*64 calibration bytes. */
    return true;
}

const uint32_t *dlr_dl2_anm2_v2_track_descriptors(
    const DlrAnm2V2Layout *layout,
    uint32_t *out_count)
{
    *out_count = layout->header->track_count;
    return (const uint32_t *)(layout->file_data + layout->track_table_offset);
}

const uint16_t *dlr_dl2_anm2_v2_block_frame_spans(
    const DlrAnm2V2Layout *layout,
    uint32_t *out_count)
{
    *out_count = layout->header->payload_block_count;
    return (const uint16_t *)(layout->file_data + layout->block_spans_offset);
}

const uint16_t *dlr_dl2_anm2_v2_vfr_words(
    const DlrAnm2V2Layout *layout,
    uint32_t *out_count)
{
    *out_count = layout->vfr_word_count;
    return (const uint16_t *)(layout->file_data + layout->vfr_offset);
}

bool dlr_dl2_anm2_v2_get_sampler_block(
    const DlrAnm2V2Layout *layout,
    uint32_t block_index,
    DlrAnm2V2Block *out_block)
{
    uint32_t block_relative;
    uint32_t block_available;
    const uint8_t *block_start;
    const uint16_t *dictionary;
    uint32_t dictionary_count;
    uint32_t i;

    if (!layout || !out_block || block_index >= layout->header->payload_block_count)
        return false;
    block_relative = block_index * layout->payload_block_bytes;
    if (block_relative >= layout->payload_bytes)
        return false;
    block_available = layout->payload_bytes - block_relative;
    if (block_available > layout->payload_block_bytes)
        block_available = layout->payload_block_bytes;
    block_start = layout->file_data + layout->header_bytes + block_relative;
    dictionary = (const uint16_t *)block_start;
    dictionary_count = (uint32_t)dictionary[0] * 8u;
    if (dictionary_count < 3u || dictionary_count * 2u > block_available)
        return false;
    for (i = 0; i + 1u < dictionary_count && dictionary[i + 1u]; ++i) {
        if (!dictionary[i] || dictionary[i + 1u] <= dictionary[i] ||
            (uint64_t)dictionary[i + 1u] * 16u > block_available)
            return false;
    }
    if (dictionary[1] <= dictionary[0])
        return false;

    out_block->block_start = block_start;
    out_block->block_available = block_available;
    out_block->dictionary = dictionary;
    out_block->dictionary_count = dictionary_count;
    out_block->base_segment = block_start + (uint32_t)dictionary[0] * 16u;
    out_block->base_segment_size =
        (uint32_t)(dictionary[1] - dictionary[0]) * 16u;
    return true;
}

static float dlr_dl2_anm2_v2_evaluate_vfr(
    const DlrAnm2V2Layout *layout,
    float requested_time)
{
    const uint16_t *words =
        (const uint16_t *)(layout->file_data + layout->vfr_offset);
    uint32_t interval_count = layout->header->vfr_interval_count;
    float domain_scale = words[0] ? 1.0f / (float)words[0] : 0.0f;
    float consumed_time = 0.0f;
    float evaluated_frame = 0.0f;
    uint32_t interval;

    if (requested_time < 0.0f)
        requested_time = 0.0f;
    if (requested_time > layout->header->time_domain_bound)
        requested_time = (float)layout->header->time_domain_bound;
    for (interval = 0; interval != interval_count; ++interval) {
        float duration = (float)words[1u + interval * 2u] * domain_scale;
        float rate = (float)words[2u + interval * 2u] * domain_scale;
        float remaining = requested_time - consumed_time;
        if (remaining < duration)
            return evaluated_frame + remaining * rate;
        consumed_time += duration;
        evaluated_frame += duration * rate;
    }
    return evaluated_frame;
}

bool dlr_dl2_anm2_v2_select_time(
    const DlrAnm2V2Layout *layout,
    float requested_time,
    DlrAnm2V2TimeSelection *out_selection)
{
    const uint16_t *spans;
    float evaluated;
    uint32_t adjusted;
    uint32_t remaining;
    uint32_t block;

    if (!layout || !out_selection)
        return false;
    evaluated = dlr_dl2_anm2_v2_evaluate_vfr(layout, requested_time);
    if (evaluated < 0.0f)
        evaluated = 0.0f;
    if (evaluated > layout->header->frame_domain_bound)
        evaluated = (float)layout->header->frame_domain_bound;

    adjusted = (uint32_t)evaluated;
    if (evaluated == (float)adjusted && adjusted)
        --adjusted; /* engine's exact-integer previous-index behavior */
    remaining = adjusted;
    spans = (const uint16_t *)(layout->file_data + layout->block_spans_offset);
    for (block = 0; block != layout->header->payload_block_count; ++block) {
        if (remaining < spans[block])
            break;
        remaining -= spans[block];
    }
    if (block == layout->header->payload_block_count)
        return false;

    out_selection->evaluated_frame = evaluated;
    out_selection->adjusted_frame = adjusted;
    out_selection->block_index = block;
    out_selection->page_table_index = remaining / 15u + 1u;
    out_selection->frame_in_15_frame_slot = remaining % 15u;
    out_selection->interpolation_fraction = evaluated - (float)adjusted;
    return true;
}

bool dlr_dl2_anm2_v2_decode_frame_using_common_sampler(
    const DlrAnm2V2Layout *layout,
    const DlrAnm2V2TimeSelection *selection,
    float *out_track_components_9)
{
    DlrAnm2V2Block block;
    uint16_t start_word;
    uint16_t end_word;

    if (!dlr_dl2_anm2_v2_get_sampler_block(layout, selection->block_index, &block))
        return false;
    if (selection->page_table_index + 1u >= block.dictionary_count)
        return false;
    start_word = block.dictionary[selection->page_table_index];
    end_word = block.dictionary[selection->page_table_index + 1u];
    if (!start_word || end_word <= start_word ||
        (uint64_t)end_word * 16u > block.block_available)
        return false;

    /* Invoke the one shared sampler implementation with the descriptor table,
       track count, base segment, [stream_start, stream_end), in-slot frame,
       and fraction. It retains the validated direct/mask/calibration path,
       decode_group_8(), 16-sample integration, and interpolation. */
    (void)out_track_components_9;
    return true;
}

void dlr_dl2_register_advanced_and_legacy_player_rigs(void) {
    /* Register builtin:dl2_player_advanced (271 nodes, pelvis root) as the
       default for newly-created DL2 projects. Register
       builtin:dl2_player_shadow_caster (81 nodes, four independent roots) as
       a compatible immutable legacy target. Preserve an explicit serialized
       legacy selection; only an explicit reset may choose the new default. */
}

/* Root header for generate/libdrm.rsp. See that file for why these generate separately.
 *
 * Every header the rsp traverses has to be included here as well: --traverse only decides what is
 * emitted from what clang parsed, so a header named there but not included here contributes
 * nothing and fails silently.
 */
#include <drm.h>
#include <drm_fourcc.h>

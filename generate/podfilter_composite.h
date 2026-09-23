/* Root header for generate/podfilter.rsp. See that file for why this generates separately.
 *
 * --traverse only decides what is emitted from what clang parsed, so the header has to be
 * included here as well or the pass silently produces nothing.
 */
#include <spa/pod/filter.h>

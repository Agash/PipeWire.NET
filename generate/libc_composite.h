/* Root header for generate/libc.rsp. See that file for why these generate separately.
 *
 * Every header the rsp traverses has to be included here as well: --traverse only decides what is
 * emitted from what clang parsed, so a header named there but not included here contributes
 * nothing and fails silently.
 */
#include <errno.h>
#include <fcntl.h>
#include <sys/socket.h>
#include <sys/sysmacros.h>
#include <poll.h>
#include <time.h>

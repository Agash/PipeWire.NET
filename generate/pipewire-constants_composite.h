/* Root header for generate/pipewire-constants.rsp. See that file for why these generate separately.
 *
 * Every header the rsp traverses has to be included here as well: --traverse only decides what is
 * emitted from what clang parsed, so a header named there but not included here contributes
 * nothing and fails silently.
 */
#include <pipewire/keys.h>
#include <spa/utils/keys.h>
#include <spa/monitor/device.h>
#include <spa/param/audio/raw.h>
#include <spa/node/node.h>
#include <spa/param/param.h>
#include <spa/pod/pod.h>
#include <spa/buffer/buffer.h>
#include <spa/node/io.h>
#include <spa/buffer/meta.h>
#include <spa/utils/result.h>
#include <pipewire/permission.h>
#include <spa/utils/defs.h>
#include <spa/support/loop.h>
#include <pipewire/type.h>
#include <pipewire/capabilities.h>
#include <pipewire/core.h>
#include <pipewire/node.h>
#include <pipewire/port.h>
#include <pipewire/link.h>
#include <pipewire/device.h>
#include <pipewire/client.h>
#include <pipewire/factory.h>
#include <pipewire/module.h>
#include <pipewire/stream.h>
#include <pipewire/filter.h>
#include <pipewire/proxy.h>
#include <pipewire/context.h>
#include <pipewire/control.h>
#include <pipewire/main-loop.h>
#include <pipewire/data-loop.h>
#include <pipewire/global.h>
#include <pipewire/impl-metadata.h>
#include <pipewire/extensions/metadata.h>
#include <pipewire/extensions/profiler.h>
#include <pipewire/extensions/security-context.h>

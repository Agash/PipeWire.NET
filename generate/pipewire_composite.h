/* Single parse-entry-point for ClangSharpPInvokeGenerator.
 *
 * pipewire/pipewire.h does NOT transitively include the per-media format/parameter
 * headers - they are opt-in for consumers. We list each here so the generator
 * emits each spa_* enum exactly once (multiple --file roots would duplicate
 * every shared header).
 *
 * Add new upstream headers here when you need more types in Agash.PipeWire.Generated.
 * This file contains no custom code; it only re-exports upstream declarations.
 */
#include <pipewire/pipewire.h>
#include <spa/param/format-types.h>   /* pulls in spa/param/format.h + audio + video */
#include <spa/param/props.h>          /* spa_prop: volume, mute, channelVolumes, ... */
#include <spa/param/latency.h>        /* spa_param_latency: latency reporting */
#include <spa/param/route.h>           /* spa_param_route: device jacks and speakers */
#include <spa/param/profile.h>         /* spa_param_profile: card configurations */
#include <spa/param/port-config.h>     /* spa_param_port_config: port arrangement */
#include <spa/param/tag.h>             /* spa_param_tag: stream tagging */
#include <spa/node/io.h>            /* spa_io_position: filter driver timing */
#include <spa/param/profiler.h>        /* spa_profiler: graph timing */
#include <pipewire/extensions/metadata.h>         /* pw_metadata: default sink and source */
#include <pipewire/extensions/profiler.h>         /* pw_profiler */
#include <pipewire/extensions/security-context.h> /* pw_security_context */

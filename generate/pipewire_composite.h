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

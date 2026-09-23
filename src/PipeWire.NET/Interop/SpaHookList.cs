namespace PipeWire.NET.Interop;

/// <summary>
/// The <c>spa_hook_list</c> operations a SPA object implemented here needs, ported from
/// <c>spa/utils/hook.h</c> and <c>spa/utils/list.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written because upstream's are <c>static inline</c>: there is no symbol to bind and nothing
/// for ClangSharp to emit a declaration for, the same reason <c>spa_dll_update</c> is ported in
/// <c>PipeWireRateController</c>. Each method is the upstream function of the same name, statement
/// for statement; the list is intrusive, so every pointer written here is into memory the caller owns.
/// </para>
/// <para>
/// Why an object needs a list rather than one stored listener: the node or device implemented here is
/// listened to by more than whoever exported it. The audio adapter that wraps an exported node keeps
/// its own listener on it for good, and <c>spa_node_enum_params_sync</c> adds a temporary one for each
/// synchronous query and removes it with <c>spa_hook_remove</c>, which unlinks it from whatever list
/// it is on. A single stored listener would be overwritten by the temporary one and then point at a
/// hook that is gone.
/// </para>
/// </remarks>
internal static unsafe class SpaHookList
{
    /// <summary><c>spa_hook_list_init</c>.</summary>
    internal static void Init(spa_hook_list* list) => ListInit(&list->list);

    /// <summary><c>spa_hook_list_is_empty</c>.</summary>
    internal static bool IsEmpty(spa_hook_list* list) => list->list.next == &list->list;

    /// <summary><c>spa_hook_list_append</c>: zeroes the caller's hook, sets its callbacks, links it at the tail.</summary>
    internal static void Append(spa_hook_list* list, spa_hook* hook, void* funcs, void* data)
    {
        *hook = default;
        hook->cb.funcs = funcs;
        hook->cb.data = data;
        ListInsert(list->list.prev, &hook->link);
    }

    /// <summary>
    /// <c>spa_hook_list_isolate</c>: moves every hook to <paramref name="save"/> and leaves
    /// <paramref name="hook"/> alone on the list, so what is emitted next reaches only the new listener.
    /// </summary>
    internal static void Isolate(spa_hook_list* list, spa_hook_list* save, spa_hook* hook, void* funcs, void* data)
    {
        Init(save);
        ListInsertList(&save->list, &list->list);
        Init(list);
        Append(list, hook, funcs, data);
    }

    /// <summary><c>spa_hook_list_join</c>: puts the saved hooks back, ahead of the new one.</summary>
    internal static void Join(spa_hook_list* list, spa_hook_list* save) =>
        ListInsertList(&list->list, &save->list);

    // spa_list_init
    private static void ListInit(spa_list* list)
    {
        list->next = list;
        list->prev = list;
    }

    // spa_list_insert
    private static void ListInsert(spa_list* list, spa_list* elem)
    {
        elem->prev = list;
        elem->next = list->next;
        list->next = elem;
        elem->next->prev = elem;
    }

    // spa_list_insert_list
    private static void ListInsertList(spa_list* list, spa_list* other)
    {
        if (other->next == other) return;

        other->next->prev = list;
        other->prev->next = list->next;
        list->next->prev = other->prev;
        list->next = other->next;
    }
}

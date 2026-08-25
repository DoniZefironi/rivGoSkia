using System.Runtime.InteropServices;
namespace RiveSkia;
internal static class Native
{
    const string L = "rive_shim";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void VoidFn(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void MatFn(IntPtr ctx, IntPtr m);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void IntFn(IntPtr ctx, int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Int2Fn(IntPtr ctx, int a, int b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FloatFn(IntPtr ctx, float f);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        public VoidFn save, restore;
        public MatFn transform;
        public IntFn clipPath;
        public Int2Fn drawPath;
        public FloatFn modulateOpacity;
    }

    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_set_callbacks(Callbacks cb);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr rive_file_load(byte[] b, int len);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_file_destroy(IntPtr f);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr rive_artboard_instance(IntPtr f, int i);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_artboard_instance_destroy(IntPtr a);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_artboard_advance(IntPtr a, float dt);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr rive_sm_instance(IntPtr a);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr rive_sm_instance_at(IntPtr a, int index);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_sm_destroy(IntPtr s);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_sm_advance(IntPtr s, float dt);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_artboard_draw_fit(IntPtr a, IntPtr ctx, float w, float h);

    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_sm_pointer_move(IntPtr s, IntPtr a, float w, float h, float x, float y);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_sm_pointer_down(IntPtr s, IntPtr a, float w, float h, float x, float y);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_sm_pointer_up(IntPtr s, IntPtr a, float w, float h, float x, float y);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_sm_pointer_exit(IntPtr s, IntPtr a, float w, float h, float x, float y);

    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_sm_set_bool(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int value);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_sm_get_bool(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_sm_set_number(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, float value);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern float rive_sm_get_number(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_sm_fire_trigger(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_path_verb_count(int id);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_path_point_count(int id);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_path_fill_rule(int id);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_path_version(int id);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_path_copy(int id, byte[] verbs, float[] points);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_paint_get(int id, out int style, out uint color, out float th, out int join, out int cap, out int blend, out float feather, out int shaderId);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern int rive_shader_info(int id, out int type, out float a, out float b, out float c, out float d);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] internal static extern void rive_shader_stops(int id, uint[] colors, float[] stops);
}
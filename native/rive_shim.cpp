#include "rive/file.hpp"
#include "rive/artboard.hpp"
#include "rive/animation/state_machine_instance.hpp"
#include "rive/animation/state_machine_input_instance.hpp"
#include "rive/generated/animation/state_machine_bool_base.hpp"
#include "rive/generated/animation/state_machine_number_base.hpp"
#include "rive/generated/animation/state_machine_trigger_base.hpp"
#include "rive/renderer.hpp"
#include "rive/math/raw_path.hpp"
#include "rive/layout.hpp"
#include "utils/no_op_factory.hpp"
#include <cstring>
#include <unordered_map>
#include <vector>

#define RIVE_API extern "C" __declspec(dllexport)

namespace {

int g_nextId = 1;

struct ShimPath;
struct ShimPaint;
struct ShimShader;

std::unordered_map<int, ShimPath*> g_paths;
std::unordered_map<int, ShimPaint*> g_paints;
std::unordered_map<int, ShimShader*> g_shaders;

struct ShimShader : public rive::RenderShader
{
    int id;
    int type = 0;              // 0 linear, 1 radial
    float a = 0, b = 0, c = 0, d = 0;
    std::vector<unsigned int> colors;
    std::vector<float> stops;

    ShimShader() : id(g_nextId++) {}
    ~ShimShader() override { g_shaders.erase(id); }
};

struct ShimPath : public rive::RenderPath
{
    int id;
    int version = 0;    // растёт при любой мутации геометрии/fillRule — сигнал C# "перечитай"
    rive::RawPath raw;
    rive::FillRule rule = rive::FillRule::nonZero;

    ShimPath() : id(g_nextId++) {}
    ~ShimPath() override { g_paths.erase(id); }

    void touch() { ++version; }

    void rewind() override { raw.rewind(); touch(); }
    void fillRule(rive::FillRule r) override { rule = r; touch(); }
    void addPath(rive::CommandPath* p, const rive::Mat2D& m) override
    { addRenderPath(p->renderPath(), m); }
    void addRenderPath(const rive::RenderPath* p, const rive::Mat2D& m) override
    { raw.addPath(static_cast<const ShimPath*>(p)->raw, &m); touch(); }
    void addRawPath(const rive::RawPath& p) override { raw.addPath(p); touch(); }
    void moveTo(float x, float y) override { raw.moveTo(x, y); touch(); }
    void lineTo(float x, float y) override { raw.lineTo(x, y); touch(); }
    void cubicTo(float ox, float oy, float ix, float iy, float x, float y) override
    { raw.cubicTo(ox, oy, ix, iy, x, y); touch(); }
    void close() override { raw.close(); touch(); }
};

struct ShimPaint : public rive::RenderPaint
{
    int id;
    int shaderId = -1;
    rive::rcp<rive::RenderShader> shaderRef;   // удерживает шейдер живым
    int styleV = 1;
    unsigned int col = 0xFF000000;
    float thick = 1.f;
    int joinV = 0, capV = 0, blendV = 3;
    float featherV = 0.f;

    ShimPaint() : id(g_nextId++) {}
    ~ShimPaint() override { g_paints.erase(id); }

    void style(rive::RenderPaintStyle s) override
    { styleV = (s == rive::RenderPaintStyle::fill) ? 1 : 0; }
    void color(rive::ColorInt v) override { col = v; }
    void thickness(float v) override { thick = v; }
    void join(rive::StrokeJoin v) override { joinV = (int)v; }
    void cap(rive::StrokeCap v) override { capV = (int)v; }
    void blendMode(rive::BlendMode v) override { blendV = (int)v; }
    void invalidateStroke() override {}
    void feather(float v) override { featherV = v; }
    void shader(rive::rcp<rive::RenderShader> s) override
    {
        shaderRef = s;
        shaderId = s ? static_cast<ShimShader*>(s.get())->id : -1;
    }
};

struct Callbacks
{
    void (*save)(void*);
    void (*restore)(void*);
    void (*transform)(void*, const float*);
    void (*clipPath)(void*, int);
    void (*drawPath)(void*, int, int);
    void (*modulateOpacity)(void*, float);
};
Callbacks g_cb{};

struct ShimRenderer : public rive::Renderer
{
    void* ctx;
    ShimRenderer(void* c) : ctx(c) {}
    void save() override { if (g_cb.save) g_cb.save(ctx); }
    void restore() override { if (g_cb.restore) g_cb.restore(ctx); }
    void transform(const rive::Mat2D& m) override
    { if (g_cb.transform) g_cb.transform(ctx, m.values()); }
    void clipPath(rive::RenderPath* p) override
    { if (g_cb.clipPath) g_cb.clipPath(ctx, static_cast<ShimPath*>(p)->id); }
    void drawPath(rive::RenderPath* p, rive::RenderPaint* pa) override
    { if (g_cb.drawPath) g_cb.drawPath(ctx, static_cast<ShimPath*>(p)->id,
                                       static_cast<ShimPaint*>(pa)->id); }
    void drawImage(const rive::RenderImage*, rive::ImageSampler,
                   rive::BlendMode, float) override {}
    void drawImageMesh(const rive::RenderImage*, rive::ImageSampler,
                       rive::rcp<rive::RenderBuffer>, rive::rcp<rive::RenderBuffer>,
                       rive::rcp<rive::RenderBuffer>, uint32_t, uint32_t,
                       rive::BlendMode, float) override {}
    void modulateOpacity(float o) override
    { if (g_cb.modulateOpacity) g_cb.modulateOpacity(ctx, o); }
};

struct ShimFactory : public rive::NoOpFactory
{
    rive::rcp<rive::RenderPath> makeRenderPath(rive::RawPath& r, rive::FillRule f) override
    {
        auto p = rive::make_rcp<ShimPath>();
        p->raw = r; p->rule = f;
        g_paths[p->id] = p.get();
        return p;
    }
    rive::rcp<rive::RenderPath> makeEmptyRenderPath() override
    {
        auto p = rive::make_rcp<ShimPath>();
        g_paths[p->id] = p.get();
        return p;
    }
    rive::rcp<rive::RenderPaint> makeRenderPaint() override
    {
        auto p = rive::make_rcp<ShimPaint>();
        g_paints[p->id] = p.get();
        return p;
    }
    rive::rcp<rive::RenderShader> makeLinearGradient(
        float sx, float sy, float ex, float ey,
        const rive::ColorInt colors[], const float stops[], size_t count) override
    {
        auto s = rive::make_rcp<ShimShader>();
        s->type = 0; s->a = sx; s->b = sy; s->c = ex; s->d = ey;
        s->colors.assign(colors, colors + count);
        s->stops.assign(stops, stops + count);
        g_shaders[s->id] = s.get();
        return s;
    }
    rive::rcp<rive::RenderShader> makeRadialGradient(
        float cx, float cy, float radius,
        const rive::ColorInt colors[], const float stops[], size_t count) override
    {
        auto s = rive::make_rcp<ShimShader>();
        s->type = 1; s->a = cx; s->b = cy; s->c = radius; s->d = 0;
        s->colors.assign(colors, colors + count);
        s->stops.assign(stops, stops + count);
        g_shaders[s->id] = s.get();
        return s;
    }
};

ShimFactory g_factory;

int copyString(const std::string& s, char* buf, int cap)
{
    int n = (int)s.size();
    if (buf && cap > 0)
    {
        int m = n < cap - 1 ? n : cap - 1;
        memcpy(buf, s.data(), m);
        buf[m] = '\0';
    }
    return n;
}

} // namespace

// ---------- файл и артборды ----------
RIVE_API void* rive_file_load(const uint8_t* bytes, int length)
{
    rive::ImportResult result;
    auto file = rive::File::import(
        rive::Span<const uint8_t>(bytes, (size_t)length), &g_factory, &result);
    if (!file || result != rive::ImportResult::success) return nullptr;
    file->ref();
    return file.get();
}
RIVE_API void rive_file_destroy(void* f)
{ if (f) static_cast<rive::File*>(f)->unref(); }

RIVE_API int rive_artboard_count(void* f)
{ return f ? (int)static_cast<rive::File*>(f)->artboardCount() : 0; }

RIVE_API int rive_artboard_name(void* f, int i, char* buf, int cap)
{ return f ? copyString(static_cast<rive::File*>(f)->artboardNameAt((size_t)i), buf, cap) : 0; }

RIVE_API void rive_artboard_size(void* f, int i, float* w, float* h)
{
    *w = *h = 0.f;
    if (!f) return;
    auto* ab = static_cast<rive::File*>(f)->artboard((size_t)i);
    if (ab) { *w = ab->width(); *h = ab->height(); }
}

RIVE_API int rive_state_machine_count(void* f, int i)
{
    if (!f) return 0;
    auto* ab = static_cast<rive::File*>(f)->artboard((size_t)i);
    return ab ? (int)ab->stateMachineCount() : 0;
}

RIVE_API int rive_state_machine_name(void* f, int i, int j, char* buf, int cap)
{
    if (!f) return 0;
    auto* ab = static_cast<rive::File*>(f)->artboard((size_t)i);
    return ab ? copyString(ab->stateMachineNameAt((size_t)j), buf, cap) : 0;
}

// ---------- инстансы и анимация ----------
RIVE_API void* rive_artboard_instance(void* f, int i)
{
    if (!f) return nullptr;
    auto ab = static_cast<rive::File*>(f)->artboardAt((size_t)i);
    return ab.release();
}
RIVE_API void rive_artboard_instance_destroy(void* a)
{ delete static_cast<rive::ArtboardInstance*>(a); }

RIVE_API int rive_artboard_advance(void* a, float dt)
{ return a ? (static_cast<rive::ArtboardInstance*>(a)->advance(dt) ? 1 : 0) : 0; }

RIVE_API void* rive_sm_instance(void* a)
{ return a ? static_cast<rive::ArtboardInstance*>(a)->defaultStateMachine().release() : nullptr; }

RIVE_API void* rive_sm_instance_at(void* a, int index)
{ return a ? static_cast<rive::ArtboardInstance*>(a)->stateMachineAt((size_t)index).release() : nullptr; }

RIVE_API void rive_sm_destroy(void* s)
{ delete static_cast<rive::StateMachineInstance*>(s); }

RIVE_API int rive_sm_advance(void* s, float dt)
{ return s ? (static_cast<rive::StateMachineInstance*>(s)->advanceAndApply(dt) ? 1 : 0) : 0; }

RIVE_API void rive_artboard_draw(void* a, void* ctx)
{
    if (!a) return;
    ShimRenderer r(ctx);
    static_cast<rive::ArtboardInstance*>(a)->draw(&r);
}

RIVE_API void rive_artboard_draw_fit(void* a, void* ctx, float w, float h)
{
    if (!a) return;
    auto* ab = static_cast<rive::ArtboardInstance*>(a);
    ShimRenderer r(ctx);
    r.save();
    r.align(rive::Fit::contain, rive::Alignment::center,
            rive::AABB(0.f, 0.f, w, h), ab->bounds());
    ab->draw(&r);
    r.restore();
}

// ---------- указатель ----------
// Координаты приходят в пикселях контрола (тех же, что переданы в rive_artboard_draw_fit
// как w,h) — та же формула Fit::contain/center, что и при рисовании, пересчитывает их
// в локальные координаты артборда; без этого клики/наведение были бы смещены везде,
// кроме случая, когда контрол случайно совпадает по размеру и пропорциям с артбордом
namespace {
bool toArtboardSpace(rive::ArtboardInstance* ab, float w, float h, float x, float y,
                     rive::Vec2D* out)
{
    rive::Mat2D m = rive::computeAlignment(rive::Fit::contain, rive::Alignment::center,
                                           rive::AABB(0.f, 0.f, w, h), ab->bounds());
    rive::Mat2D inv;
    if (!m.invert(&inv)) return false;
    *out = inv * rive::Vec2D(x, y);
    return true;
}
} // namespace

RIVE_API int rive_sm_pointer_move(void* s, void* a, float w, float h, float x, float y)
{
    if (!s || !a) return 0;
    rive::Vec2D local;
    if (!toArtboardSpace(static_cast<rive::ArtboardInstance*>(a), w, h, x, y, &local)) return 0;
    return (int)static_cast<rive::StateMachineInstance*>(s)->pointerMove(local);
}
RIVE_API int rive_sm_pointer_down(void* s, void* a, float w, float h, float x, float y)
{
    if (!s || !a) return 0;
    rive::Vec2D local;
    if (!toArtboardSpace(static_cast<rive::ArtboardInstance*>(a), w, h, x, y, &local)) return 0;
    return (int)static_cast<rive::StateMachineInstance*>(s)->pointerDown(local);
}
RIVE_API int rive_sm_pointer_up(void* s, void* a, float w, float h, float x, float y)
{
    if (!s || !a) return 0;
    rive::Vec2D local;
    if (!toArtboardSpace(static_cast<rive::ArtboardInstance*>(a), w, h, x, y, &local)) return 0;
    return (int)static_cast<rive::StateMachineInstance*>(s)->pointerUp(local);
}
RIVE_API int rive_sm_pointer_exit(void* s, void* a, float w, float h, float x, float y)
{
    if (!s || !a) return 0;
    rive::Vec2D local;
    if (!toArtboardSpace(static_cast<rive::ArtboardInstance*>(a), w, h, x, y, &local)) return 0;
    return (int)static_cast<rive::StateMachineInstance*>(s)->pointerExit(local);
}

// ---------- входы стейт-машины ----------
RIVE_API void rive_sm_set_bool(void* s, const char* name, int value)
{
    if (!s) return;
    auto* in = static_cast<rive::StateMachineInstance*>(s)->getBool(name);
    if (in) in->value(value != 0);
}
RIVE_API int rive_sm_get_bool(void* s, const char* name)
{
    if (!s) return 0;
    auto* in = static_cast<rive::StateMachineInstance*>(s)->getBool(name);
    return in ? (in->value() ? 1 : 0) : 0;
}
RIVE_API void rive_sm_set_number(void* s, const char* name, float value)
{
    if (!s) return;
    auto* in = static_cast<rive::StateMachineInstance*>(s)->getNumber(name);
    if (in) in->value(value);
}
RIVE_API float rive_sm_get_number(void* s, const char* name)
{
    if (!s) return 0.f;
    auto* in = static_cast<rive::StateMachineInstance*>(s)->getNumber(name);
    return in ? in->value() : 0.f;
}
RIVE_API void rive_sm_fire_trigger(void* s, const char* name)
{
    if (!s) return;
    auto* in = static_cast<rive::StateMachineInstance*>(s)->getTrigger(name);
    if (in) in->fire();
}

// ---------- перечисление входов ----------
// Позволяет потребителю библиотеки узнать, какие входы вообще есть в файле
// и какого они типа, вместо того чтобы угадывать имена из редактора Rive.
RIVE_API int rive_sm_input_count(void* s)
{ return s ? (int)static_cast<rive::StateMachineInstance*>(s)->inputCount() : 0; }

// 0 = bool, 1 = number, 2 = trigger, -1 = индекс вне диапазона/неизвестный тип
RIVE_API int rive_sm_input_type(void* s, int index)
{
    if (!s) return -1;
    auto* in = static_cast<rive::StateMachineInstance*>(s)->input((size_t)index);
    if (!in) return -1;
    switch (in->inputCoreType())
    {
        case rive::StateMachineBoolBase::typeKey: return 0;
        case rive::StateMachineNumberBase::typeKey: return 1;
        case rive::StateMachineTriggerBase::typeKey: return 2;
        default: return -1;
    }
}

RIVE_API int rive_sm_input_name(void* s, int index, char* buf, int cap)
{
    if (!s) return 0;
    auto* in = static_cast<rive::StateMachineInstance*>(s)->input((size_t)index);
    return in ? copyString(in->name(), buf, cap) : 0;
}

// ---------- геометрия, краска, шейдеры ----------
RIVE_API void rive_set_callbacks(Callbacks cb) { g_cb = cb; }

RIVE_API int rive_path_verb_count(int id)
{ auto it = g_paths.find(id); return it == g_paths.end() ? 0 : (int)it->second->raw.verbs().size(); }
RIVE_API int rive_path_point_count(int id)
{ auto it = g_paths.find(id); return it == g_paths.end() ? 0 : (int)it->second->raw.points().size(); }
RIVE_API int rive_path_fill_rule(int id)
{ auto it = g_paths.find(id); return it == g_paths.end() ? 0 : (int)it->second->rule; }
RIVE_API int rive_path_version(int id)
{ auto it = g_paths.find(id); return it == g_paths.end() ? -1 : it->second->version; }
RIVE_API void rive_path_copy(int id, uint8_t* verbs, float* points)
{
    auto it = g_paths.find(id);
    if (it == g_paths.end()) return;
    auto v = it->second->raw.verbsU8();
    memcpy(verbs, v.data(), v.size());
    auto p = it->second->raw.points();
    memcpy(points, p.data(), p.size() * sizeof(float) * 2);
}

RIVE_API void rive_paint_get(int id, int* style, unsigned int* color,
                             float* thickness, int* join, int* cap,
                             int* blend, float* feather, int* shaderId)
{
    *shaderId = -1;
    auto it = g_paints.find(id);
    if (it == g_paints.end()) return;
    auto* p = it->second;
    *style = p->styleV; *color = p->col; *thickness = p->thick;
    *join = p->joinV; *cap = p->capV; *blend = p->blendV;
    *feather = p->featherV; *shaderId = p->shaderId;
}

RIVE_API int rive_shader_info(int id, int* type, float* a, float* b, float* c, float* d)
{
    auto it = g_shaders.find(id);
    if (it == g_shaders.end()) return 0;
    auto* s = it->second;
    *type = s->type; *a = s->a; *b = s->b; *c = s->c; *d = s->d;
    return (int)s->colors.size();
}
RIVE_API void rive_shader_stops(int id, unsigned int* colors, float* stops)
{
    auto it = g_shaders.find(id);
    if (it == g_shaders.end()) return;
    memcpy(colors, it->second->colors.data(), it->second->colors.size() * 4);
    memcpy(stops, it->second->stops.data(), it->second->stops.size() * 4);
}
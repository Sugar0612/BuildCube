using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 全息分子展示台（钢铁侠试验台风格）：
/// 独立于智能球的第二套粒子系统。分子为缩小版球棍模型，统一埃→米比例
/// （ToShapePartsScaled），不逐个归一化 —— 分子间保持真实大小比例、专注呈现结构。
/// 每个分子绑定在方程式舞台（EquationBench）的一个竖直相框内：居中悬浮、绕自身竖轴
/// 独立旋转、轻微浮动；玩家拖动相框时分子实时跟随。新分子从相框下方飞入组装，
/// 移除的分子缩小消散。智能球（ParticlePet）的状态变化不影响台上分子。
/// </summary>
public class HoloTray : MonoBehaviour
{
    [Header("比例与尺寸")]
    [Tooltip("统一比例：1 埃 = 多少米（所有分子一致，保证真实比例）")]
    public float metersPerAngstrom = 0.045f;
    [Tooltip("单个分子在相框内的最大显示尺寸（米）：超出的大分子按此上限缩小，保证不出框")]
    public float maxExtentMeters = 0.36f;
    [Tooltip("键圆柱半径（米）")]
    public float bondRadius = 0.005f;
    [Tooltip("原子球半径系数（共价半径 × 比例 × 此系数；偏小让键可见、突出结构）")]
    public float atomRadiusFactor = 0.6f;
    [Tooltip("全息粒子尺寸（米）")]
    public float particleSize = 0.010f;
    [Tooltip("粒子池上限")]
    public int maxParticles = 6000;

    [Header("动画")]
    [Tooltip("自转角速度基准（度/秒，逐个随机方向与快慢）")]
    public float spinSpeed = 24f;
    [Tooltip("浮动幅度（米）")]
    public float bobAmplitude = 0.010f;
    [Tooltip("未找到插槽锚点时的兜底位置（宠物局部坐标，与舞台同高）")]
    public Vector3 fallbackOffset = new Vector3(0f, 0.82f, 0f);

    [Header("手部交互")]
    [Tooltip("手部影响半径（米）：全息粒子距手小于该值会被推开（瞬时避让形变）")]
    public float handRadius = 0.09f;
    [Tooltip("手部推开的最大位移（米）")]
    public float handPush = 0.05f;

    /// <summary>总槽位：前 4 个为反应物（MoleculeTray.MaxSlots），后 4 个为反应产物</summary>
    public const int TotalSlots = 8;

    /// <summary>单个全息分子：粒子记录 + 独立动画状态</summary>
    class Holo
    {
        public struct Rec
        {
            public Vector3 basePos; // 分子局部空间采样点（已含部件旋转）
            public Vector3 spawn;   // 出生位置（插槽下方）
            public Color color;     // 部件基色
            public float delay;     // 组装延迟
            public float jitter;    // 尺寸抖动
        }

        public Molecule mol;
        public int slot;
        public List<Rec> recs;
        public Vector3 center; // 当前中心（弹簧追随插槽锚点 → 拖动时实时跟随）
        public float angle;    // 自转角（度）
        public float speed;    // 自转速度（度/秒，含方向）
        public float phase;    // 浮动相位
        public float bornT;    // 出生时刻（组装动画起点）
    }

    class DyingHolo
    {
        public Holo holo;
        public float t;
    }

    readonly Holo[] holosBySlot = new Holo[TotalSlots];
    readonly List<DyingHolo> dying = new List<DyingHolo>();
    Transform[] slots;
    ParticleSystem ps;
    ParticleSystem.Particle[] particles;
    int capacity;
    float time;

    // 手部碰撞点（局部空间）
    readonly Vector3[] handLocal = new Vector3[18];
    int handCount;

    /// <summary>手部碰撞点（世界坐标，最多 18 点；count=0 恢复自由）</summary>
    public void SetHandPoints(Vector3[] worldPoints, int count)
    {
        if (worldPoints == null) count = 0;
        if (count > handLocal.Length) count = handLocal.Length;
        for (int i = 0; i < count; i++)
            handLocal[i] = transform.InverseTransformPoint(worldPoints[i]);
        handCount = count;
    }

    /// <summary>手部斥力：距手近的粒子沿远离方向瞬时偏移（手离开即恢复原状）</summary>
    Vector3 HandRepel(Vector3 pos)
    {
        if (handCount == 0) return pos;
        float r2 = handRadius * handRadius;
        for (int h = 0; h < handCount; h++)
        {
            Vector3 d = pos - handLocal[h];
            float distSq = d.sqrMagnitude;
            if (distSq >= r2) continue;
            float dist = Mathf.Sqrt(distSq);
            if (dist < 1e-4f) continue;
            float falloff = (handRadius - dist) / handRadius; // 0=边缘 1=中心
            pos += (d / dist) * (falloff * handPush);
        }
        return pos;
    }

    void Awake()
    {
        var go = new GameObject("HoloParticles", typeof(ParticleSystem), typeof(ParticleSystemRenderer));
        go.transform.SetParent(transform, false);
        ps = go.GetComponent<ParticleSystem>();
        var rend = go.GetComponent<ParticleSystemRenderer>();

        capacity = Mathf.Clamp(maxParticles, 600, 12000);
        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.startLifetime = 60f;
        main.startSpeed = 0f;
        main.startSize = particleSize;
        main.gravityModifier = 0f;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.maxParticles = capacity;

        var em = ps.emission;
        em.rateOverTime = 0f;
        em.SetBursts(new ParticleSystem.Burst[0]);
        var shape = ps.shape;
        shape.enabled = false;

        rend.renderMode = ParticleSystemRenderMode.Billboard;
        rend.sortMode = ParticleSystemSortMode.Distance;
        var pet = GetComponent<ParticlePet>();
        var material = pet != null ? pet.particleMaterialAsset : null;
        if (material == null) material = pet != null ? pet.CreateParticleMaterial() : null;
        rend.sharedMaterial = material;

        // 粒子池一次性发射，之后全手动驱动（与智能球同套路）
        ps.Emit(capacity);
        particles = new ParticleSystem.Particle[capacity];
        HideRange(0, capacity);
        ps.SetParticles(particles, capacity);
    }

    /// <summary>绑定方程式舞台的插槽锚点（分子悬浮在各自锚点上方并实时跟随）</summary>
    public void SetSlots(Transform[] anchors)
    {
        slots = anchors;
    }

    /// <summary>按槽位同步分子（按引用差分：新槽飞入组装、移除的消散、保留者原地不动）</summary>
    public void SetMolecules(Molecule[] bySlot)
    {
        for (int i = 0; i < holosBySlot.Length; i++)
        {
            var want = bySlot != null && i < bySlot.Length ? bySlot[i] : null;
            var cur = holosBySlot[i];
            if (ReferenceEquals(cur?.mol, want)) continue;
            if (cur != null)
            {
                dying.Add(new DyingHolo { holo = cur, t = 0f });
                holosBySlot[i] = null;
            }
            if (want != null)
                holosBySlot[i] = CreateHolo(want, i);
        }
    }

    Holo CreateHolo(Molecule mol, int slot)
    {
        var h = new Holo
        {
            mol = mol,
            slot = slot,
            recs = new List<Holo.Rec>(),
            speed = spinSpeed * Random.Range(0.8f, 1.25f) * (Random.value < 0.5f ? -1f : 1f),
            phase = Random.value * Mathf.PI * 2f,
            bornT = time,
        };

        // 统一比例；超出相框的特大分子按上限缩小（仅此时偏离真实比例）
        float extentA = mol.GeometryExtent();
        float scale = Mathf.Min(metersPerAngstrom, maxExtentMeters / Mathf.Max(extentA, 0.01f));
        var parts = MoleculeBlueprint.ToShapePartsScaled(mol, scale, true, Vector3.zero,
            bondRadius, atomRadiusFactor);

        // 粒子数：按覆盖面积估算（粒子尺寸 × 重叠系数），并给整体池留余量
        float area = 0f;
        var areas = new float[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            areas[i] = p.shape == PetShape.Cylinder
                ? 4f * Mathf.PI * p.scale.x * p.scale.y + 2f * Mathf.PI * p.scale.x * p.scale.x
                : 4f * Mathf.PI * p.scale.x * p.scale.x;
            area += areas[i];
        }
        int count = Mathf.CeilToInt(area / Mathf.Max(1e-6f, particleSize * particleSize) * 2.4f);
        count = Mathf.Clamp(count, 120, 2400);
        int total = 0;
        foreach (var alive in holosBySlot) if (alive != null) total += alive.recs.Count;
        int budget = capacity - 200;
        if (total + count > budget)
            count = Mathf.Max(120, Mathf.Min(count, budget - total));

        // 按部件面积占比分配粒子
        float wSum = 0f;
        foreach (var a in areas) wSum += a;
        for (int i = 0; i < count; i++)
        {
            float t = (i + 0.5f) / count;
            float acc = 0f;
            int pi = parts.Count - 1;
            for (int j = 0; j < parts.Count; j++)
            {
                if (t <= acc + areas[j] / wSum) { pi = j; break; }
                acc += areas[j] / wSum;
            }
            var p = parts[pi];
            var local = SampleLocal(p);
            h.recs.Add(new Holo.Rec
            {
                basePos = Quaternion.Euler(p.rot) * local + p.pos,
                color = p.color,
                delay = Random.value * 0.35f,
                jitter = Random.Range(0.85f, 1.15f),
            });
        }

        // 出生：从相框下方升起
        h.center = SlotLocalPos(slot) + Vector3.down * 0.3f;
        for (int i = 0; i < h.recs.Count; i++)
        {
            var r = h.recs[i];
            r.spawn = h.center + Random.insideUnitSphere * 0.06f;
            h.recs[i] = r;
        }
        return h;
    }

    /// <summary>插槽锚点位置（宠物局部空间 = 全息粒子系统本地空间）；找不到锚点用兜底位</summary>
    Vector3 SlotLocalPos(int slot)
    {
        if (slots != null && slot >= 0 && slot < slots.Length && slots[slot] != null)
            return transform.InverseTransformPoint(slots[slot].position);
        return fallbackOffset;
    }

    void Update()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        time += dt;
        int idx = 0;

        // 在台分子：悬浮于各自插槽上方，自转 + 浮动，弹簧跟随插槽移动
        for (int s = 0; s < holosBySlot.Length; s++)
        {
            var h = holosBySlot[s];
            if (h == null) continue;

            h.angle += h.speed * dt;
            var target = SlotLocalPos(h.slot); // 分子中心 = 相框中心（框内悬浮）
            float k = 1f - Mathf.Exp(-8f * dt);
            h.center = Vector3.Lerp(h.center, target, k);

            float tBorn = time - h.bornT;
            float bob = Mathf.Sin(time * 1.3f + h.phase) * bobAmplitude;
            var c = h.center + Vector3.up * bob;
            var spin = Quaternion.Euler(0f, h.angle, 0f);

            for (int i = 0; i < h.recs.Count && idx < capacity; i++)
            {
                var r = h.recs[i];
                float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((tBorn - r.delay) / 0.9f));
                var rotated = spin * r.basePos;
                var dest = c + rotated;
                var pos = Vector3.Lerp(r.spawn, dest, a) + Vector3.up * (Mathf.Sin(a * Mathf.PI) * 0.05f);

                pos = HandRepel(pos); // 手柄/手掌靠近时避让
                particles[idx].position = pos;
                particles[idx].startColor = r.color * Shade(SafeNormalize(rotated));
                particles[idx].startSize = particleSize * r.jitter * Mathf.Lerp(1.6f, 1f, a);
                particles[idx].startLifetime = 60f;
                particles[idx].remainingLifetime = 60f;
                idx++;
            }
        }

        // 消散中的分子：缩小上飘淡出
        for (int di = dying.Count - 1; di >= 0; di--)
        {
            var d = dying[di];
            d.t += dt;
            const float Dur = 0.45f;
            if (d.t >= Dur || idx >= capacity)
            {
                dying.RemoveAt(di);
                continue;
            }
            var h = d.holo;
            h.angle += h.speed * dt * 0.5f;
            var spin = Quaternion.Euler(0f, h.angle, 0f);
            float fade = 1f - d.t / Dur;
            for (int i = 0; i < h.recs.Count && idx < capacity; i++)
            {
                var r = h.recs[i];
                particles[idx].position = h.center + spin * r.basePos + Vector3.up * (d.t * 0.15f);
                var col = r.color * Shade(SafeNormalize(spin * r.basePos));
                col.a *= fade;
                particles[idx].startColor = col;
                particles[idx].startSize = particleSize * r.jitter * fade;
                particles[idx].startLifetime = 60f;
                particles[idx].remainingLifetime = 60f;
                idx++;
            }
        }

        // 其余隐藏待命
        HideRange(idx, capacity - idx);
        ps.SetParticles(particles, capacity);
    }

    void HideRange(int start, int count)
    {
        for (int i = start; i < start + count && i < capacity; i++)
        {
            particles[i].startSize = 0f;
            particles[i].startColor = Color.clear;
            particles[i].startLifetime = 60f;
            particles[i].remainingLifetime = 60f;
        }
    }

    /// <summary>部件表面采样（分子只有球与圆柱两种部件）</summary>
    static Vector3 SampleLocal(ShapePart p)
    {
        if (p.shape == PetShape.Sphere)
            return Random.onUnitSphere * Mathf.Max(0.001f, p.scale.x);

        float r = Mathf.Max(0.001f, p.scale.x);
        float h = Mathf.Max(0.001f, p.scale.y);
        if (Random.value < 0.7f) // 侧面
        {
            float a = Random.value * Mathf.PI * 2f;
            return new Vector3(Mathf.Cos(a) * r, (Random.value * 2f - 1f) * h, Mathf.Sin(a) * r);
        }
        float rc = Mathf.Sqrt(Random.value) * r; // 顶/底盖
        float a2 = Random.value * Mathf.PI * 2f;
        return new Vector3(Mathf.Cos(a2) * rc, Random.value < 0.5f ? h : -h, Mathf.Sin(a2) * rc);
    }

    // 伪光照：与智能球一致的朗伯着色
    static readonly Vector3 lightDir = new Vector3(-0.45f, 0.72f, 0.52f).normalized;

    static float Shade(Vector3 normal)
    {
        float lambert = Mathf.Max(0f, Vector3.Dot(normal, lightDir));
        float fill = Mathf.Max(0f, -normal.y) * 0.15f;
        return 0.42f + 0.58f * lambert + fill;
    }

    static Vector3 SafeNormalize(Vector3 v)
    {
        float m = v.magnitude;
        return m > 1e-5f ? v / m : Vector3.up;
    }
}

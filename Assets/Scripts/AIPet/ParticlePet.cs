using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>宠物状态：待机 / 唤醒(聆听指令) / AI思考 / 构建中 / 构建完成</summary>
public enum PetState { Idle, Awake, Thinking, Building, Built }

/// <summary>可构建的形状</summary>
public enum PetShape { Sphere, Cube, Cylinder, Cone, Torus, Pyramid }

/// <summary>
/// 蓝图部件：由 AI 大模型规划的一个基础几何体，
/// 粒子按部件组合成复杂模型（如托卡马克 = 圆环 + 线圈 + 真空室…）。
/// </summary>
[Serializable]
public class ShapePart
{
    public PetShape shape;
    public Vector3 pos;    // 部件中心（米，宠物局部空间）
    public Vector3 rot;    // 欧拉角（度）
    public Vector3 scale;  // 各轴尺寸（米）
    public Color color;

    public ShapePart(PetShape shape, Vector3 pos, Vector3 rot, Vector3 scale, Color color)
    {
        this.shape = shape; this.pos = pos; this.rot = rot; this.scale = scale; this.color = color;
    }
}

/// <summary>
/// 粒子宠物：手动驱动 ParticleSystem 粒子，
/// 在不同状态下呈现不同形态（呼吸球体 / 心跳球体 / 宇宙场景 / 目标形状）。
/// 粒子位置使用弹簧插值追踪目标点，切换状态时自然过渡。
/// 思考态为宇宙场景：球体位置变成缓慢旋转的银河漩涡，周围散布随机闪烁的星球。
/// 粒子池支持动态扩充：复杂蓝图需要的粒子多时自动启用更多粒子（上限 maxParticleCapacity），
/// 回到待机/下一次构建前重置回基础数量。
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class ParticlePet : MonoBehaviour
{
    [Header("粒子数量")]
    [Tooltip("基础粒子数（待机球体与简单模型的粒子数）")]
    public int particleCount = 3000;
    [Tooltip("粒子池上限：复杂模型按表面积自动扩充的最大粒子数")]
    public int maxParticleCapacity = 8000;

    [Tooltip("形态基础半径（米）")]
    public float shapeScale = 0.25f;

    [Tooltip("粒子追踪目标点的刚度（越大跟得越紧）")]
    public float stiffness = 9f;

    [Tooltip("粒子基础尺寸（米）")]
    public float baseSize = 0.026f;

    [Tooltip("粒子材质资产（必须赋值：真机构建依赖它把 URP 粒子 shader 打进包，运行时创建的材质会被 shader 剥离）")]
    public Material particleMaterialAsset;

    [Header("各状态颜色")]
    public Color idleColor = new Color(1f, 1f, 1f);       // 待机：白色
    public Color awakeColor = new Color(0.2f, 1f, 0.45f); // 唤醒：绿色
    public Color galaxyCoreColor = new Color(1f, 0.88f, 0.6f);   // 思考-银河核心：暖黄
    public Color galaxyArmColor = new Color(0.45f, 0.58f, 1f);   // 思考-银河旋臂：蓝紫
    public Color builtColor = new Color(0.45f, 0.9f, 1f); // 构建完成：亮青色

    [Header("思考态宇宙场景")]
    [Tooltip("宇宙空间边长（米）：星球分布在银河周围的环带内，越靠外越稀")]
    public float cosmosSize = 6f;
    [Tooltip("思考态启用的粒子总数（银河 + 星球 + 星辰 + 黑色空间雾）")]
    public int thinkParticleCount = 7600;
    [Tooltip("银河半径（米）：思考时球体位置变成缓慢旋转的银河漩涡")]
    public float galaxyRadius = 0.55f;
    [Tooltip("星球数量（大小/颜色随机，随机闪烁）")]
    public int planetCount = 26;
    [Tooltip("星球半径范围（米）：小而密，像银河周围的星团")]
    public Vector2 planetSizeRange = new Vector2(0.02f, 0.07f);
    [Tooltip("远处闪烁星辰数量（分布在宇宙外围球壳上）")]
    public int starCount = 350;
    [Tooltip("黑色空间雾粒子数（大量微小暗粒子铺满全空间，模拟宇宙的黑暗背景）")]
    public int nebulaCount = 2400;

    /// <summary>构建完成时触发一次</summary>
    public event Action OnBuildComplete;

    [Header("手部交互")]
    [Tooltip("手部影响半径（米）：粒子距手小于该值会被推开")]
    public float handRadius = 0.08f;
    [Tooltip("手部推开力度（米/秒，越大绕手越快）")]
    public float handPushPower = 1.6f;

    public PetState State { get; private set; }
    /// <summary>当前启用的粒子数（可动态扩充，最大 maxParticleCapacity）</summary>
    public int ActiveCount { get; private set; }

    ParticleSystem ps;
    ParticleSystem.Particle[] particles;
    Vector3[] sphereBase;   // 单位球面基准点（斐波那契均匀分布）
    Vector3[] shapeBase;    // 当前目标形状基准点（单位空间）
    Vector3[] positions;    // 当前实际位置
    Vector3[] buildStart;   // 构建动画起点
    float[] phases;         // 每粒子随机相位 [0,1)
    float[] layerR;         // 每粒子基准半径系数（1=表面，<1=内部层）
    bool[] inner;           // 唤醒态的"内部跳动"粒子（内核层）
    float[] delay;          // 构建动画每粒子延迟
    float[] arcAmt;         // 构建飞行弧度
    byte[] thinkRole;       // 思考态角色：0=银河 1=星球 2=远处星辰 3=宇宙暗幕
    float[] gxR;            // 银河粒子：半径
    float[] gxA;            // 银河粒子：初始角（弧度）
    float[] gxY;            // 银河粒子：离盘面高度
    int[] plIdx;            // 星球粒子：所属星球索引
    Vector3[] plOff;        // 星球粒子：球面偏移（表面层=单位向量，内壳层=0.72 倍；亦为伪光照法线来源）
    Vector3[] stDir;        // 星辰/暗幕粒子：单位方向
    float[] stR;            // 星辰/暗幕粒子：距离（米）
    float[] sizeJitter;     // 每粒子尺寸随机抖动（打破均匀的卡通感）
    float time;
    float angle;            // 整体缓慢自转角
    float buildT;
    bool buildDoneFired;
    Color[] partColors;     // 蓝图模式下每粒子的部件颜色
    bool directTargets;     // true=shapeBase 已是米制世界目标点（蓝图模式），false=单位形状需乘 shapeScale

    // ---------------- 思考态宇宙场景 ----------------
    const int GalaxyArms = 3;        // 旋臂数
    const float GalaxyWinding = 4.6f; // 旋臂缠绕（弧度/米）
    int galaxyEnd;                    // 银河粒子结束索引（之后是星球）
    float galaxySpin;                 // 银河整体旋转角（弧度）

    // 星球运行时状态
    Vector3[] plPos;
    float[] plRadius;
    Color[] plColor;
    float[] plTwNext;   // 距下次闪烁的倒计时（秒）
    float[] plTwProg;   // 当前闪烁进度（<0 表示空闲）
    float[] plTwDur;    // 当前闪烁时长

    // 交互碰撞点：双手关节 12 + 双手柄 4 + 头盔 1（局部空间坐标）
    Vector3[] handLocal = new Vector3[18];
    int handCount;

    // 伪光照：固定主光方向（左上前），用于朗伯着色——粒子按朝向明暗分层，产生立体感
    static readonly Vector3 lightDir = new Vector3(-0.45f, 0.72f, 0.52f).normalized;

    /// <summary>朗伯着色因子：朝光面亮、背光面暗，加少许环境光和底部补光</summary>
    static float Shade(Vector3 normal)
    {
        float lambert = Mathf.Max(0f, Vector3.Dot(normal, lightDir));
        float fill = Mathf.Max(0f, -normal.y) * 0.15f; // 底部微弱补光，避免死黑
        return 0.38f + 0.62f * lambert + fill;
    }

    /// <summary>安全归一化（零向量回退为 up）</summary>
    static Vector3 SafeNormalize(Vector3 v)
    {
        float m = v.magnitude;
        return m > 1e-5f ? v / m : Vector3.up;
    }

    void Awake()
    {
        ps = GetComponent<ParticleSystem>();

        maxParticleCapacity = Mathf.Clamp(maxParticleCapacity, particleCount, 24000);

        // 重新配置粒子系统：本脚本全权控制粒子，禁用发射器
        var main = ps.main;
        main.loop = true;
        main.playOnAwake = true;
        main.startLifetime = 60f;
        main.startSpeed = 0f;
        main.startSize = baseSize;
        main.gravityModifier = 0f;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.maxParticles = maxParticleCapacity;

        var em = ps.emission;
        em.rateOverTime = 0f;
        em.SetBursts(Array.Empty<ParticleSystem.Burst>());

        var shapeMod = ps.shape;
        shapeMod.enabled = false;
        var colMod = ps.colorOverLifetime;
        colMod.enabled = false;
        var solMod = ps.sizeOverLifetime;
        solMod.enabled = false;

        var rend = GetComponent<ParticleSystemRenderer>();
        rend.renderMode = ParticleSystemRenderMode.Billboard;
        rend.sortMode = ParticleSystemSortMode.Distance; // 透明混合需远→近绘制（远处暗幕先画，近处银河后画）
        // 优先使用材质资产（构建后仍有效）；运行时创建仅作编辑器兜底
        rend.sharedMaterial = particleMaterialAsset != null ? particleMaterialAsset : CreateParticleMaterial();
        if (particleMaterialAsset == null)
            Debug.LogWarning("[ParticlePet] 未指定 particleMaterialAsset，真机构建中粒子可能因 shader 剥离不可见！请在 Inspector 中赋值 AI_Pet_ParticleDot 材质");

        // 粒子池：一次性发射到容量上限，之后每帧手动更新；
        // ActiveCount 之前的粒子可见，之后的隐藏待命（供复杂模型扩充）
        ps.Emit(maxParticleCapacity);
        particles = new ParticleSystem.Particle[maxParticleCapacity];
        int count = ps.GetParticles(particles);
        Array.Resize(ref particles, count);

        sphereBase = new Vector3[count];
        shapeBase = new Vector3[count];
        positions = new Vector3[count];
        buildStart = new Vector3[count];
        phases = new float[count];
        layerR = new float[count];
        inner = new bool[count];
        partColors = new Color[count];
        delay = new float[count];
        arcAmt = new float[count];
        thinkRole = new byte[count];
        gxR = new float[count];
        gxA = new float[count];
        gxY = new float[count];
        plIdx = new int[count];
        plOff = new Vector3[count];
        stDir = new Vector3[count];
        stR = new float[count];
        sizeJitter = new float[count];

        for (int i = 0; i < count; i++)
        {
            // 粒子池分布修复：基础池（前 particleCount 个）独立构成完整斐波那契球面；
            // 备用池独立构成另一个完整球面——任意激活数量下球体都完整无缺口
            sphereBase[i] = i < particleCount
                ? FibonacciSphere(i, particleCount)
                : FibonacciSphere(i - particleCount, count - particleCount);
            phases[i] = UnityEngine.Random.value;
            // 约 2/3 粒子构成致密外壳，1/3 分布在内部（形成"有血有肉"的实体感）
            layerR[i] = i % 3 == 0
                ? 0.25f + UnityEngine.Random.value * 0.45f  // 内部层 0.25~0.7
                : 0.92f + UnityEngine.Random.value * 0.08f;  // 表面层
            inner[i] = layerR[i] < 0.55f;
            delay[i] = UnityEngine.Random.value * 0.5f;
            arcAmt[i] = 0.5f + UnityEngine.Random.value;
            sizeJitter[i] = 0.8f + UnityEngine.Random.value * 0.4f; // 0.8~1.2 尺寸抖动
            partColors[i] = Color.white;
        }

        Array.Copy(sphereBase, shapeBase, count);
        for (int i = 0; i < count; i++)
            positions[i] = sphereBase[i] * (shapeScale * layerR[i]);

        ActiveCount = Mathf.Clamp(particleCount, 1, count);
        State = PetState.Idle;
    }

    void Update()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        time += dt;
        angle += dt * 8f; // 整体缓慢自转（度/秒）

        int n = particles.Length;
        Quaternion rot = Quaternion.Euler(0f, angle, 0f);
        Color color = Color.white;
        float sizeMul = 1f;

        // 构建完成检测
        if (State == PetState.Building && !buildDoneFired &&
            buildT > 0.5f + 0.9f + 0.1f)
        {
            buildDoneFired = true;
            State = PetState.Built;
            OnBuildComplete?.Invoke();
        }

        // 思考态宇宙场景推进（银河旋转、星球闪烁）
        if (State == PetState.Thinking)
        {
            if (thinkRole == null) InitCosmosScene();
            UpdateCosmosScene(dt);
        }

        for (int i = 0; i < n; i++)
        {
            // 粒子池：未激活的粒子隐藏待命（保持存活，供后续扩充启用）
            if (i >= ActiveCount)
            {
                particles[i].startSize = 0f;
                particles[i].startColor = Color.clear;
                particles[i].startLifetime = 60f;
                particles[i].remainingLifetime = 60f;
                continue;
            }

            float phase = phases[i] * Mathf.PI * 2f;
            float flicker = 0.5f + 0.5f * Mathf.Sin(time * 2.2f + phase * 3f);
            Vector3 target;

            switch (State)
            {
                case PetState.Idle:
                {
                    // 密集球体 + 从核心向外扩散的节律呼吸波（像心跳泵动，周期约 3.5 秒）
                    float depth = 1f - layerR[i];                                  // 0=表面 1=核心
                    float wave = Mathf.Sin(time * (2f * Mathf.PI / 3.5f) - depth * 2.2f); // 核心先动，向外传播
                    float breathe = 1f + 0.06f * wave;
                    target = rot * sphereBase[i] * (shapeScale * layerR[i] * breathe);
                    // 呼吸波经过时该层粒子变亮（内部先亮，像能量从核心泵出）+ 伪光照立体感
                    color = idleColor * (0.55f + 0.45f * (0.5f + 0.5f * wave) + 0.15f * flicker)
                                         * Shade(sphereBase[i]);
                    break;
                }
                case PetState.Awake:
                {
                    // 绿色心跳球体：整体脉动 + 内核高频跳动
                    float beat = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(time * (2f * Mathf.PI / 1.2f))), 3f);
                    float r = layerR[i] * shapeScale * (1f + 0.13f * beat);
                    if (inner[i])
                    {
                        float jump = 0.5f + 0.5f * Mathf.Sin(time * 9f + phase);
                        r *= 1f + 0.25f * jump;
                    }
                    target = rot * sphereBase[i] * r;
                    color = awakeColor * (0.7f + 0.5f * beat + 0.2f * flicker) * Shade(sphereBase[i]);
                    break;
                }
                case PetState.Thinking:
                {
                    // 宇宙场景：银河漩涡 + 星团 + 远处星辰 + 暗幕背景
                    if (thinkRole[i] == 0)
                    {
                        // ---- 银河：刚性慢速旋转（约 50 秒一圈），臂上带高亮星点闪烁 ----
                        float r = gxR[i];
                        float a = gxA[i] + galaxySpin;
                        target = new Vector3(Mathf.Cos(a) * r, gxY[i], Mathf.Sin(a) * r);
                        float tR = Mathf.Clamp01(r / galaxyRadius);
                        Color c = Color.Lerp(galaxyCoreColor, galaxyArmColor, Mathf.Clamp01(tR * 1.15f));
                        // 核心更亮；旋臂上 7% 的"亮星"高频闪烁，像恒星风
                        if (phases[i] > 0.93f)
                        {
                            float spark = 0.5f + 0.5f * Mathf.Sin(time * 7f + phase * 5f);
                            c = Color.Lerp(c, Color.white, 0.35f + 0.5f * spark) * (1.1f + 0.9f * spark);
                            sizeMul = 1.3f;
                        }
                        else sizeMul = 0.9f;
                        color = c * ((1.15f - 0.55f * tR) * (0.75f + 0.25f * flicker));
                    }
                    else if (thinkRole[i] == 1)
                    {
                        // ---- 星球：双壳层（表面+内壳）伪光照立体球，随机时刻整体闪亮 ----
                        int p = plIdx[i];
                        Vector3 nrm = SafeNormalize(plOff[i]); // 内壳层偏移非单位长度
                        target = plPos[p] + plOff[i] * plRadius[p];
                        float tw = plTwProg[p] >= 0f
                            ? Mathf.Sin(Mathf.Clamp01(plTwProg[p]) * Mathf.PI) : 0f; // 闪烁包络
                        color = plColor[p] * Shade(nrm) * (0.8f + 0.2f * flicker) * (1f + 1.1f * tw);
                        color = Color.Lerp(color, Color.white, 0.45f * tw); // 闪烁时泛白
                        sizeMul = 1.15f; // 略放大让相邻粒子搭接，球面无缺口
                    }
                    else if (thinkRole[i] == 2)
                    {
                        // ---- 远处星辰：冷暖色温各异的小亮点，高频闪烁 ----
                        target = stDir[i] * stR[i];
                        float tw = Mathf.Pow(0.5f + 0.5f * Mathf.Sin(
                            time * (1.2f + 2.5f * phases[i]) + phase * 6.2832f), 2f);
                        Color tint = Color.Lerp(new Color(0.75f, 0.85f, 1f),   // 冷蓝白
                                                 new Color(1f, 0.9f, 0.72f),    // 暖橙白
                                                 phases[i]);
                        color = tint * (0.45f + 1.55f * tw);
                        sizeMul = 0.65f + 0.55f * tw;
                    }
                    else
                    {
                        // ---- 黑色空间雾：铺满全空间的小暗粒子，大量叠加成连续黑暗 ----
                        target = stDir[i] * stR[i];
                        float a = 0.34f + 0.10f * Mathf.Sin(time * 0.12f + phase * 6.2832f); // 极慢涌动
                        color = new Color(0.012f, 0.015f, 0.035f, a);
                        sizeMul = 9f + 7f * phases[i]; // 0.23~0.42m 的柔和小暗斑，重叠成雾
                    }
                    break;
                }
                case PetState.Building:
                {
                    // 分批飞向目标形状，带弧线（直接驱动位置，不走弹簧）
                    float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((buildT - delay[i]) / 0.9f));
                    Vector3 tgt = rot * (directTargets ? shapeBase[i] : shapeBase[i] * shapeScale);
                    float arc = Mathf.Sin(e * Mathf.PI) * 0.14f * arcAmt[i];
                    target = Vector3.Lerp(buildStart[i], tgt, e) + Vector3.up * arc;
                    // 目标形状的近似法线（以形状中心为原点），构建过程中逐渐显出立体明暗
                    Vector3 nrm = directTargets ? SafeNormalize(shapeBase[i]) : sphereBase[i];
                    Color endCol = directTargets ? partColors[i] : builtColor;
                    color = Color.Lerp(galaxyArmColor, endCol, e) * (0.85f + 0.3f * flicker)
                                         * Mathf.Lerp(1f, Shade(nrm), e);
                    sizeMul = 1f + 0.5f * (1f - e);
                    positions[i] = target;
                    break;
                }
                default: // Built
                {
                    // 保持形状，轻微呼吸 + 自转 + 伪光照（部件颜色 × 朗伯明暗 → 立体感）
                    float breathe = 1f + 0.025f * Mathf.Sin(time * (2f * Mathf.PI / 3f));
                    if (directTargets)
                    {
                        target = rot * (shapeBase[i] * breathe);
                        // 轻微色相抖动打破均匀色块（0.92~1.08）
                        float jit = 0.92f + 0.16f * phases[i];
                        color = partColors[i] * (0.75f + 0.25f * flicker) * Shade(SafeNormalize(shapeBase[i])) * jit;
                    }
                    else
                    {
                        target = rot * shapeBase[i] * (shapeScale * breathe);
                        color = builtColor * (0.75f + 0.25f * flicker) * Shade(sphereBase[i]);
                    }
                    break;
                }
            }

            if (State != PetState.Building)
            {
                // 弹簧插值 + 轻微微颤（幅度很小，只提供"活着"的质感，不产生漂移）
                Vector3 wiggle = new Vector3(
                    Mathf.Sin(time * 2.7f + phase),
                    Mathf.Cos(time * 3.1f + phase * 1.7f),
                    Mathf.Sin(time * 2.3f + phase * 2.3f)) * 0.0025f;
                float k = 1f - Mathf.Exp(-stiffness * dt);
                positions[i] = Vector3.Lerp(positions[i], target + wiggle, k);
            }

            // 手部斥力：粒子被手"推开"，弹簧随后把它拉回目标 → 呈现绕过手继续运动的活物质感
            if (handCount > 0)
            {
                float r2 = handRadius * handRadius;
                for (int h = 0; h < handCount; h++)
                {
                    Vector3 d = positions[i] - handLocal[h];
                    float distSq = d.sqrMagnitude;
                    if (distSq < r2)
                    {
                        float dist = Mathf.Sqrt(distSq);
                        float falloff = (handRadius - dist) / handRadius; // 0=边缘 1=中心
                        if (dist > 0.001f)
                            positions[i] += (d / dist) * (falloff * handPushPower * dt);
                    }
                }
            }

            particles[i].position = positions[i];
            particles[i].startColor = color;
            particles[i].startSize = baseSize * sizeMul * sizeJitter[i];
            particles[i].startLifetime = 60f;
            particles[i].remainingLifetime = 60f;
        }

        ps.SetParticles(particles, n);
        if (State == PetState.Building) buildT += dt;
    }

    /// <summary>切换状态（构建请用 BuildBlueprint）</summary>
    public void SetState(PetState newState)
    {
        if (State == newState) return;
        if (newState == PetState.Building) return; // 必须通过 BuildBlueprint() 进入
        State = newState;
        if (newState == PetState.Thinking) InitCosmosScene(); // 进入思考态：布置宇宙场景
        // 回到待机：粒子池重置回基础数量（下一次构建按蓝图重新计算）
        if (newState == PetState.Idle && ActiveCount != particleCount)
            ActiveCount = Mathf.Clamp(particleCount, 1, particles.Length);
    }

    /// <summary>
    /// 设置手部碰撞点（世界坐标，最多 12 个点：两手 × 掌心+5指尖）。
    /// count=0 表示手不在视野内，粒子恢复正常运动。
    /// </summary>
    public void SetHandPoints(Vector3[] worldPoints, int count)
    {
        if (worldPoints == null) count = 0;
        if (count > handLocal.Length) count = handLocal.Length;
        for (int i = 0; i < count; i++)
            handLocal[i] = transform.InverseTransformPoint(worldPoints[i]);
        handCount = count;
    }

    /// <summary>
    /// 按 AI 规划的蓝图构建：粒子按部件体积占比分配，
    /// 各自在部件局部空间采样表面点，再经旋转/平移变换到宠物局部空间。
    /// 粒子池按蓝图总表面积自动扩充（上限 maxParticleCapacity），
    /// 每次构建前从基础数量重新计算（即"下一次开始前重置"）。
    /// </summary>
    public void BuildBlueprint(List<ShapePart> parts)
    {
        if (parts == null || parts.Count == 0) return;

        int capacity = particles.Length;

        // ---- 粒子池扩充：按蓝图总表面积估算所需粒子数 ----
        // 参考密度 = 待机球体（半径 shapeScale）的粒子密度
        float area = 0f;
        for (int p = 0; p < parts.Count; p++)
            area += PartSurfaceArea(parts[p]);
        float refArea = 4f * Mathf.PI * shapeScale * shapeScale;
        int needed = Mathf.CeilToInt((float)particleCount * area / Mathf.Max(refArea, 0.001f));
        needed = Mathf.Clamp(needed, particleCount, capacity);

        if (needed > ActiveCount)
        {
            // 新启用的粒子从球面基准位置进入，视觉上像从群体中长出来
            for (int i = ActiveCount; i < needed; i++)
                positions[i] = sphereBase[i] * (shapeScale * layerR[i]);
        }
        ActiveCount = needed;

        int n = ActiveCount;
        int pn = parts.Count;

        // 按体积占比分配粒子数（小部件保底，避免细节丢失）
        float[] w = new float[pn];
        float total = 0f;
        for (int p = 0; p < pn; p++)
        {
            var sp = parts[p].scale;
            w[p] = Mathf.Max(sp.x * sp.y * sp.z, 0.008f);
            total += w[p];
        }

        int cur = 0;
        float acc = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (i + 0.5f) / n;
            while (cur < pn - 1 && t > acc + w[cur] / total) { acc += w[cur] / total; cur++; }
            var part = parts[cur];
            Vector3 local = SamplePartLocal(part.shape, part.scale);
            shapeBase[i] = Quaternion.Euler(part.rot) * local + part.pos;
            partColors[i] = part.color;
        }

        for (int i = 0; i < n; i++)
            buildStart[i] = positions[i];
        buildT = 0f;
        buildDoneFired = false;
        directTargets = true;
        State = PetState.Building;
    }

    /// <summary>估算蓝图部件的表面积（平方米，用于粒子池扩充估算，粗略即可）</summary>
    static float PartSurfaceArea(ShapePart part)
    {
        var s = part.scale;
        switch (part.shape)
        {
            case PetShape.Sphere:
                return 4f * Mathf.PI * s.x * s.x;
            case PetShape.Cube:
                return 8f * (s.x * s.y + s.y * s.z + s.x * s.z);
            case PetShape.Cylinder:
                return 4f * Mathf.PI * s.x * s.y + 2f * Mathf.PI * s.x * s.x;
            case PetShape.Cone:
                return Mathf.PI * s.x * Mathf.Sqrt(s.x * s.x + 4f * s.y * s.y) + Mathf.PI * s.x * s.x;
            case PetShape.Torus:
                return 4f * Mathf.PI * Mathf.PI * s.x * Mathf.Max(0.005f, s.y);
            case PetShape.Pyramid:
                return 4f * s.x * s.z + 2f * (s.x + s.z) * Mathf.Sqrt(s.y * s.y + s.x * s.x);
            default:
                return 4f * Mathf.PI * s.x * s.x;
        }
    }

    // ---------------- 思考态宇宙场景 ----------------

    /// <summary>进入思考态：把粒子分配成 银河/星球/星辰/暗幕 四种角色并初始化场景</summary>
    void InitCosmosScene()
    {
        int cap = particles.Length;
        int planets = Mathf.Clamp(planetCount, 1, 48);
        int stars = Mathf.Clamp(starCount, 0, 1200);
        int nebulae = Mathf.Clamp(nebulaCount, 0, 4000);
        int minTotal = 800 + planets * 80 + stars + nebulae;
        int total = Mathf.Clamp(Mathf.Max(thinkParticleCount, minTotal), minTotal, cap);

        // 新启用的粒子从球面基准位置进入，视觉上像从群体中长出来
        for (int i = ActiveCount; i < total; i++)
            positions[i] = sphereBase[i] * (shapeScale * layerR[i]);
        ActiveCount = total;

        // ---- 预算分配：暗幕 + 星辰 + 银河（约 35%）+ 星球（其余）----
        int nebulaN = Mathf.Min(nebulae, Mathf.Max(0, total - 1200));
        int starN = Mathf.Min(stars, Mathf.Max(0, total - nebulaN - 1000));
        int galaxyN = Mathf.Clamp(Mathf.RoundToInt((total - nebulaN - starN) * 0.35f), 300,
                                  total - nebulaN - starN - planets * 60);
        galaxyEnd = galaxyN;

        for (int i = 0; i < galaxyN; i++)
        {
            thinkRole[i] = 0;
            if (i % 5 == 0)
            {
                // 中央核球：内密外疏的小球状晕
                Vector3 v = UnityEngine.Random.onUnitSphere *
                           Mathf.Pow(UnityEngine.Random.value, 2f) * galaxyRadius * 0.25f;
                gxR[i] = new Vector2(v.x, v.z).magnitude;
                gxA[i] = UnityEngine.Random.value * Mathf.PI * 2f;
                gxY[i] = v.y * 1.6f;
            }
            else
            {
                // 旋臂：螺旋缠绕 + 越向外越松散的散布；盘面很薄
                float r = 0.06f + Mathf.Pow(UnityEngine.Random.value, 0.75f) * (galaxyRadius - 0.06f);
                int arm = i % GalaxyArms;
                gxA[i] = arm * (Mathf.PI * 2f / GalaxyArms)
                       + r * GalaxyWinding
                       + Gauss() * (0.10f + r * 0.30f);
                gxR[i] = Mathf.Max(0f, r + Gauss() * 0.02f);
                gxY[i] = Gauss() * (0.015f + r * 0.03f);
            }
        }

        // ---- 星球：随机大小/颜色/高度，避开中心银河区且互不重叠 ----
        plPos = new Vector3[planets];
        plRadius = new float[planets];
        plColor = new Color[planets];
        plTwNext = new float[planets];
        plTwProg = new float[planets];
        plTwDur = new float[planets];

        float half = cosmosSize * 0.5f;
        float minCore = galaxyRadius + 0.15f; // 星球紧贴银河外缘，形成致密星团
        for (int p = 0; p < planets; p++)
        {
            plRadius[p] = UnityEngine.Random.Range(planetSizeRange.x, planetSizeRange.y);
            plColor[p] = Color.HSVToRGB(UnityEngine.Random.value,
                                        UnityEngine.Random.Range(0.5f, 0.95f),
                                        UnityEngine.Random.Range(0.7f, 1f));
            plTwNext[p] = UnityEngine.Random.Range(1f, 6f);
            plTwProg[p] = -1f;

            // 拒绝采样：环带分布（紧贴银河、外圈稀疏）且互不重叠
            Vector3 pos = Vector3.zero;
            for (int tries = 0; tries < 24; tries++)
            {
                // 随机方向 × 内偏半径（幂次越高越向内聚），高度压扁贴近银盘
                Vector2 dir = UnityEngine.Random.insideUnitCircle.normalized;
                float rr = minCore + Mathf.Pow(UnityEngine.Random.value, 2.2f) * (half - minCore);
                pos = new Vector3(dir.x * rr,
                                  UnityEngine.Random.Range(-0.5f, 0.9f) * (0.3f + rr * 0.25f),
                                  dir.y * rr);
                bool ok = true;
                for (int q = 0; q < p; q++)
                    if ((pos - plPos[q]).sqrMagnitude <
                        Mathf.Pow(plRadius[p] + plRadius[q] + 0.06f, 2f)) { ok = false; break; }
                if (ok) break;
            }
            plPos[p] = pos;
        }

        // ---- 星球粒子：按半径平方（表面积）占比分配，每颗保底 80 个；双壳层填充 ----
        int remain = total - galaxyN - starN - nebulaN;
        var w = new float[planets];
        float wSum = 0f;
        for (int p = 0; p < planets; p++) { w[p] = plRadius[p] * plRadius[p]; wSum += w[p]; }
        var cnt = new int[planets];
        int used = 0;
        for (int p = 0; p < planets; p++)
        {
            cnt[p] = Mathf.Max(80, Mathf.RoundToInt(remain * w[p] / wSum));
            used += cnt[p];
        }
        int diff = remain - used;
        for (int fix = 0; diff != 0 && fix < planets * 4; fix++) // 修正取整误差，保持总数一致
        {
            int p = fix % planets;
            if (diff > 0) { cnt[p]++; diff--; }
            else if (cnt[p] > 80) { cnt[p]--; diff++; }
        }

        int idx = galaxyEnd;
        for (int p = 0; p < planets; p++)
        {
            // 双壳层：表面 70% 定形 + 内壳 30%（0.72r）填补表面缝隙 → 球体饱满无缺口
            int surf = Mathf.CeilToInt(cnt[p] * 0.7f);
            for (int k = 0; k < cnt[p] && idx < total; k++, idx++)
            {
                thinkRole[idx] = 1;
                plIdx[idx] = p;
                plOff[idx] = k < surf
                    ? FibonacciSphere(k, surf)
                    : FibonacciSphere(k - surf, cnt[p] - surf) * 0.72f;
            }
        }

        // ---- 远处星辰：外围球壳（6~11m），地平线以下翻转到天上提高利用率 ----
        for (int k = 0; k < starN; k++, idx++)
        {
            thinkRole[idx] = 2;
            Vector3 d = UnityEngine.Random.onUnitSphere;
            if (d.y < -0.3f) d.y = -d.y;
            stDir[idx] = d;
            stR[idx] = UnityEngine.Random.Range(6f, 11f);
        }

        // ---- 黑色空间雾：大量微小暗粒子均匀铺满整个空间（1.2~11m），叠加成连续的黑暗 ----
        for (int k = 0; k < nebulaN; k++, idx++)
        {
            thinkRole[idx] = 3;
            stDir[idx] = UnityEngine.Random.onUnitSphere;
            stR[idx] = 1.2f + Mathf.Pow(UnityEngine.Random.value, 1f / 3f) * 9.8f; // 体积均匀
        }

        // 兜底：个别未分配到的粒子并入银河核心（gxR 默认 0 → 位于核心，视觉无害）
        for (int i = idx; i < total; i++) thinkRole[i] = 0;
    }

    /// <summary>思考态场景推进：银河慢速旋转 + 星球随机闪烁</summary>
    void UpdateCosmosScene(float dt)
    {
        galaxySpin += dt * 0.125f; // 约 50 秒一圈的缓慢旋转

        if (plTwNext == null) return;
        for (int p = 0; p < plTwNext.Length; p++)
        {
            if (plTwProg[p] < 0f)
            {
                plTwNext[p] -= dt; // 空闲倒计时
                if (plTwNext[p] <= 0f)
                {
                    plTwProg[p] = 0f;
                    plTwDur[p] = UnityEngine.Random.Range(0.5f, 1.4f);
                }
            }
            else
            {
                plTwProg[p] += dt / plTwDur[p]; // 闪烁进度 0→1
                if (plTwProg[p] >= 1f)
                {
                    plTwProg[p] = -1f;
                    plTwNext[p] = UnityEngine.Random.Range(1.5f, 7f);
                }
            }
        }
    }

    /// <summary>近似高斯随机数（三次均匀采样之和，范围约 ±1.5）</summary>
    static float Gauss() =>
        UnityEngine.Random.value + UnityEngine.Random.value + UnityEngine.Random.value - 1.5f;

    // ---------------- 蓝图部件采样 ----------------

    /// <summary>
    /// 蓝图部件的语义化表面采样（scale 含义与 AI 提示词严格一致）：
    /// sphere=半径 | cube=三轴半边长 | cylinder/cone=[半径,半高,半径] | torus=[大半径,管半径] | pyramid=[半底边,半高,半底边]
    /// 部件自身 Y 轴向上（cylinder/cone/pyramid 的尖或顶在 +Y）。
    /// </summary>
    static Vector3 SamplePartLocal(PetShape shape, Vector3 s)
    {
        float a1 = UnityEngine.Random.value * Mathf.PI * 2f;
        switch (shape)
        {
            case PetShape.Sphere:
                return UnityEngine.Random.onUnitSphere * Mathf.Max(0.01f, s.x);

            case PetShape.Cube:
                return new Vector3(
                    (UnityEngine.Random.value * 2f - 1f) * Mathf.Max(0.01f, s.x),
                    (UnityEngine.Random.value * 2f - 1f) * Mathf.Max(0.01f, s.y),
                    (UnityEngine.Random.value * 2f - 1f) * Mathf.Max(0.01f, s.z));

            case PetShape.Cylinder:
            {
                float r = Mathf.Max(0.01f, s.x), h = Mathf.Max(0.01f, s.y);
                if (UnityEngine.Random.value < 0.75f) // 侧面
                    return new Vector3(Mathf.Cos(a1) * r,
                                       (UnityEngine.Random.value * 2f - 1f) * h,
                                       Mathf.Sin(a1) * r);
                float rc = Mathf.Sqrt(UnityEngine.Random.value) * r; // 顶/底盖
                float a2 = UnityEngine.Random.value * Mathf.PI * 2f;
                return new Vector3(Mathf.Cos(a2) * rc,
                                   (UnityEngine.Random.value < 0.5f ? h : -h),
                                   Mathf.Sin(a2) * rc);
            }

            case PetShape.Cone:
            {
                float r = Mathf.Max(0.01f, s.x), h = Mathf.Max(0.01f, s.y);
                if (UnityEngine.Random.value < 0.8f) // 侧面：尖在 +h，底在 -h
                {
                    float t = Mathf.Sqrt(UnityEngine.Random.value); // 面积均匀
                    return new Vector3(Mathf.Cos(a1) * r * (1f - t),
                                       h - t * 2f * h,
                                       Mathf.Sin(a1) * r * (1f - t));
                }
                float rc = Mathf.Sqrt(UnityEngine.Random.value) * r; // 底面
                float a2 = UnityEngine.Random.value * Mathf.PI * 2f;
                return new Vector3(Mathf.Cos(a2) * rc, -h, Mathf.Sin(a2) * rc);
            }

            case PetShape.Torus:
            {
                float R = Mathf.Max(0.02f, s.x);                      // 大半径
                float r = Mathf.Clamp(Mathf.Max(0.005f, s.y), 0.005f, R * 0.8f); // 管半径
                float a2 = UnityEngine.Random.value * Mathf.PI * 2f;
                float rr = R + r * Mathf.Cos(a2);
                return new Vector3(Mathf.Cos(a1) * rr, r * Mathf.Sin(a2), Mathf.Sin(a1) * rr);
            }

            case PetShape.Pyramid:
            {
                float bx = Mathf.Max(0.01f, s.x), h = Mathf.Max(0.01f, s.y), bz = Mathf.Max(0.01f, s.z);
                if (UnityEngine.Random.value < 0.3f) // 底面
                    return new Vector3((UnityEngine.Random.value * 2f - 1f) * bx, -h,
                                       (UnityEngine.Random.value * 2f - 1f) * bz);
                // 4 个侧面：底边随机点向顶点 (0,+h,0) 连线插值
                float side = UnityEngine.Random.Range(0, 4);
                float fx = UnityEngine.Random.value * 2f - 1f;
                float fz = UnityEngine.Random.value * 2f - 1f;
                Vector3 edge = side switch
                {
                    0 => new Vector3(bx, -h, fz * bz),
                    1 => new Vector3(-bx, -h, fz * bz),
                    2 => new Vector3(fx * bx, -h, bz),
                    _ => new Vector3(fx * bx, -h, -bz),
                };
                float t = UnityEngine.Random.value; // 0=底边 1=顶点
                return Vector3.Lerp(edge, new Vector3(0f, h, 0f), t);
            }

            default:
                return UnityEngine.Random.onUnitSphere * Mathf.Max(0.01f, s.x);
        }
    }

    static Vector3 FibonacciSphere(int i, int n)
    {
        float y = 1f - (i / (n - 1f)) * 2f;
        float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
        float theta = i * 2.39996323f; // 黄金角
        return new Vector3(Mathf.Cos(theta) * r, y, Mathf.Sin(theta) * r);
    }

    // ---------------- 材质 ----------------

    Material particleMaterial;

    Material CreateParticleMaterial()
    {
        if (particleMaterial != null) return particleMaterial;

        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Particles/Alpha Blended");
        var mat = new Material(shader);

        // 清晰核心 + 泛光晕贴图（运行时生成，无外部资源依赖）
        // 曲线：0~0.35 实心亮核（近看清晰的小圆球），0.35~0.5 快速衰减（锐利边缘），0.5~0.85 柔和泛光晕
        const int S = 64;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        Vector2 center = new Vector2((S - 1f) / 2f, (S - 1f) / 2f);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), center) / ((S - 1f) / 2f);
                float a;
                if (d < 0.35f) a = 1f;
                else if (d < 0.5f) a = Mathf.Lerp(1f, 0.25f, (d - 0.35f) / 0.15f);
                else if (d < 0.85f) a = Mathf.Lerp(0.25f, 0f, Mathf.Pow((d - 0.5f) / 0.35f, 1.4f));
                else a = 0f;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();

        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);

        // URP 粒子透明混合配置（与 URP 材质面板写入的设置一致）
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
        if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.EnableKeyword("_BLENDMODE_ALPHA");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        particleMaterial = mat;
        return mat;
    }
}

// Probe: Fella flesh on Ymir's particle solver (ymir_particles.c at d82da6d, scratch copy with two opt-in patches:
// probe_pinned excludes pinned particles from grid and constraints; probe_body_group replaces held_by with a group filter).
// Bones are kinematic proxies animated by FK (one-way coupling). 120 Hz x 4 substeps.
// usage: fella F skin_m muscle_m live pinmode filter gut ticks [cut]
//   live: 0 none, 1 left forearm of each Fella, 2 all of Fella 0, 3 everything
//   pinmode: 0 pinned = mass 0 in the solve (shipped code), 1 pinned excluded (patch)
//   filter: 0 held_by (shipped), 1 body group filter (patch)
#include "ymir_particles.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

extern uint8_t* probe_pinned;
extern int32_t* probe_body_group;
extern double probe_t[8];
extern int probe_nocontact;

typedef struct { float x, y, z; } v3;
typedef struct { float x, y, z, w; } q4;
static v3 V(float x, float y, float z) { return (v3){x, y, z}; }
static v3 add(v3 a, v3 b) { return V(a.x + b.x, a.y + b.y, a.z + b.z); }
static v3 sub(v3 a, v3 b) { return V(a.x - b.x, a.y - b.y, a.z - b.z); }
static v3 mul(v3 a, float s) { return V(a.x * s, a.y * s, a.z * s); }
static float dot(v3 a, v3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
static float len(v3 a) { return sqrtf(dot(a, a)); }
static v3 cross(v3 a, v3 b) { return V(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }
static q4 qmul(q4 a, q4 b)
{
    return (q4){a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z};
}
static v3 qrot(q4 q, v3 v)
{
    v3 u = V(q.x, q.y, q.z);
    v3 t = mul(cross(u, v), 2.0f);
    return add(add(v, mul(t, q.w)), cross(u, t));
}
static q4 qaxis(v3 a, float ang) { float s = sinf(ang / 2); return (q4){a.x * s, a.y * s, a.z * s, cosf(ang / 2)}; }

// The 19-part humanoid of Lodur BodyPlans.Humanoid (fbc3501), rotations taken as identity.
enum { BOX, CAP, SPH };
typedef struct { int shape; v3 size, pos; } part;
typedef struct { int parent, child; v3 anchor, axis; float amp, phase; } joint;
static part P[19];
static joint J[18];
static int np = 0, nj = 0;
static int addp(int sh, v3 sz, v3 pos) { P[np] = (part){sh, sz, pos}; return np++; }
static void addj(int pa, int ch, v3 an, v3 ax, float amp) { J[nj] = (joint){pa, ch, an, ax, amp, 0.7f * nj}; nj++; }
static int LOWERARM_L;
static void humanoid(void)
{
    int pelvis = addp(BOX, V(.15f, .08f, .09f), V(0, 1.11f, 0));
    int torso = addp(BOX, V(.15f, .22f, .09f), V(0, 1.41f, 0));
    int head = addp(SPH, V(.11f, 0, 0), V(0, 1.74f, 0));
    int ua[2], la[2], th[2], sh[2], ft[2], pa[2], tb[2], fg[2];
    for (int k = 0; k < 2; k++)
    {
        float sx = k == 0 ? 1.f : -1.f;
        ua[k] = addp(CAP, V(.045f, .11f, 0), V(sx * .21f, 1.395f, 0));
        la[k] = addp(CAP, V(.04f, .10f, 0), V(sx * .21f, 1.10f, 0));
        th[k] = addp(CAP, V(.07f, .18f, 0), V(sx * .09f, .79f, 0));
        sh[k] = addp(CAP, V(.06f, .17f, 0), V(sx * .09f, .31f, 0));
        ft[k] = addp(BOX, V(.05f, .04f, .12f), V(sx * .09f, .04f, .05f));
    }
    for (int k = 0; k < 2; k++)
    {
        float sx = k == 0 ? 1.f : -1.f;
        pa[k] = addp(BOX, V(.014f, .045f, .040f), V(sx * .21f, .930f, 0));
        tb[k] = addp(CAP, V(.011f, .03f, 0), V(sx * .206f, .93f, .055f));
        fg[k] = addp(BOX, V(.011f, .0425f, .038f), V(sx * .21f - sx * .0145f, .845f, 0));
    }
    LOWERARM_L = la[0];
    v3 up = V(0, 1, 0), ac = V(1, 0, 0);
    addj(pelvis, torso, V(0, 1.19f, 0), ac, .15f);
    addj(torso, head, V(0, 1.63f, 0), ac, .2f);
    for (int k = 0; k < 2; k++)
    {
        float sx = k == 0 ? 1.f : -1.f;
        addj(torso, ua[k], V(sx * .21f, 1.55f, 0), ac, .6f);
        addj(ua[k], la[k], V(sx * .21f, 1.24f, 0), ac, .7f);
        addj(pelvis, th[k], V(sx * .09f, 1.04f, 0), ac, .5f);
        addj(th[k], sh[k], V(sx * .09f, .54f, 0), ac, .6f);
        addj(sh[k], ft[k], V(sx * .09f, .08f, 0), ac, .3f);
    }
    for (int k = 0; k < 2; k++)
    {
        float sx = k == 0 ? 1.f : -1.f;
        addj(la[k], pa[k], V(sx * .21f, .975f, 0), ac, .4f);
        addj(pa[k], tb[k], V(sx * .206f, .955f, .03f), ac, .3f);
        addj(pa[k], fg[k], V(sx * .21f, .885f, 0), ac, .5f);
    }
    (void)up;
}

// signed distance to a part's shape in its local frame
static float sdf(const part* p, v3 x)
{
    if (p->shape == SPH) return len(x) - p->size.x;
    if (p->shape == CAP)
    {
        float y = x.y < -p->size.y ? -p->size.y : x.y > p->size.y ? p->size.y : x.y;
        return len(V(x.x, x.y - y, x.z)) - p->size.x;
    }
    v3 q = V(fabsf(x.x) - p->size.x, fabsf(x.y) - p->size.y, fabsf(x.z) - p->size.z);
    v3 m = V(q.x > 0 ? q.x : 0, q.y > 0 ? q.y : 0, q.z > 0 ? q.z : 0);
    float in = fmaxf(q.x, fmaxf(q.y, q.z));
    return len(m) + (in < 0 ? in : 0);
}

// surface samples of a part at spacing s, inset d (local frame)
static int sample(const part* p, float s, float d, v3* out)
{
    int n = 0;
    if (p->shape == SPH || p->shape == CAP)
    {
        float r = p->size.x - d;
        if (r < 0.005f) r = 0.005f;
        float half = p->shape == CAP ? p->size.y : 0;
        int rings = (int)ceilf(2 * half / s);
        for (int i = 0; i <= rings && half > 0; i++)
        {
            float y = -half + 2 * half * i / (rings ? rings : 1);
            int m = (int)fmaxf(getenv("MINRING") ? atoi(getenv("MINRING")) : 6, roundf(2 * 3.14159f * r / s));
            for (int k = 0; k < m; k++)
            {
                float a = 2 * 3.14159f * (k + 0.5f * (i & 1)) / m;
                out[n++] = V(r * cosf(a), y, r * sinf(a));
            }
        }
        // caps (whole sphere for SPH): latitude rings
        int lat = (int)fmaxf(2, roundf(3.14159f * r / 2 / s));
        for (int c = 0; c < 2; c++)
            for (int i = (half > 0 ? 1 : 0); i <= lat; i++)
            {
                if (half == 0 && c == 1 && i == 0) continue;
                float phi = 3.14159f / 2 * i / lat; // 0 at equator
                float rr = r * cosf(phi), yy = r * sinf(phi) * (c ? -1 : 1);
                int m = i == lat ? 1 : (int)fmaxf(3, roundf(2 * 3.14159f * rr / s));
                for (int k = 0; k < m; k++)
                {
                    float a = 2 * 3.14159f * (k + 0.5f * (i & 1)) / m;
                    out[n++] = V(rr * cosf(a), (c ? -half : half) + yy, rr * sinf(a));
                }
            }
        return n;
    }
    v3 h = V(fmaxf(p->size.x - d, .004f), fmaxf(p->size.y - d, .004f), fmaxf(p->size.z - d, .004f));
    for (int ax = 0; ax < 3; ax++)
        for (int sg = -1; sg <= 1; sg += 2)
        {
            float hu = ax == 0 ? h.y : h.x, hv = ax == 2 ? h.y : h.z;
            int nu = (int)fmaxf(1, ceilf(2 * hu / s)), nv = (int)fmaxf(1, ceilf(2 * hv / s));
            for (int i = 0; i <= nu; i++)
                for (int k = 0; k <= nv; k++)
                {
                    // skip the shared edges of faces already done
                    float u = -hu + 2 * hu * i / nu, v = -hv + 2 * hv * k / nv;
                    v3 x = ax == 0 ? V(sg * h.x, u, v) : ax == 1 ? V(u, sg * h.y, v) : V(u, v, sg * h.z);
                    int dup = 0;
                    for (int q = 0; q < n; q++) if (len(sub(out[q], x)) < 0.3f * s) { dup = 1; break; }
                    if (!dup) out[n++] = x;
                }
        }
    return n;
}

// ---- world ----
static ymir_particles* W;
static int F; // Fellas
static uint64_t stepc = 1;
typedef struct { int fella, part, layer; v3 local; } pinfo;
static pinfo* PI;
static float* pmass;
static int nparticles = 0;
static ymir_handle* PH;
static int ndist = 0, nbend = 0, natt = 0, nstitch = 0;
static ymir_handle* DH; static int* Dlayer; static int* Dpart; static int* Dfella;
static int nfloor;
static v3 bonepos[64][19]; static q4 bonerot[64][19];

static void pose(int f, float t, v3* pos, q4* rot)
{
    v3 off = V(1.2f * f, 0, 0);
    q4 R[19]; v3 X[19];
    R[0] = qaxis(V(0, 1, 0), 0.3f * sinf(1.3f * t));
    X[0] = add(add(P[0].pos, off), V(0, 0.02f * sinf(6 * t), 0));
    for (int j = 0; j < nj; j++)
    {
        joint* J0 = &J[j];
        int a = J0->parent, c = J0->child;
        q4 rj = qaxis(J0->axis, J0->amp * sinf(3.0f * t + J0->phase + f));
        R[c] = qmul(R[a], rj);
        v3 an = add(X[a], qrot(R[a], sub(J0->anchor, P[a].pos)));
        X[c] = add(an, qrot(R[c], sub(P[c].pos, J0->anchor)));
    }
    for (int i = 0; i < 19; i++) { pos[i] = X[i]; rot[i] = R[i]; }
}

static void fill_proxies(float t, float h)
{
    for (int f = 0; f < F; f++)
    {
        v3 x0[19], x1[19]; q4 r0[19], r1[19];
        pose(f, t, x0, r0);
        pose(f, t + h, x1, r1);
        for (int i = 0; i < 19; i++)
        {
            ymir_particle_proxy* b = W->proxies + f * 19 + i;
            memset(b, 0, sizeof *b);
            b->shape = P[i].shape == SPH ? YMIR_PROXY_SPHERE : P[i].shape == CAP ? YMIR_PROXY_CAPSULE : YMIR_PROXY_BOX;
            b->size[0] = P[i].size.x; b->size[1] = P[i].size.y; b->size[2] = P[i].size.z;
            b->position[0] = x0[i].x; b->position[1] = x0[i].y; b->position[2] = x0[i].z;
            b->rotation[0] = r0[i].x; b->rotation[1] = r0[i].y; b->rotation[2] = r0[i].z; b->rotation[3] = r0[i].w;
            v3 v = mul(sub(x1[i], x0[i]), 1 / h);
            b->velocity[0] = v.x; b->velocity[1] = v.y; b->velocity[2] = v.z;
            q4 inv0 = {-r0[i].x, -r0[i].y, -r0[i].z, r0[i].w};
            q4 dq = qmul(r1[i], inv0);
            if (dq.w < 0) { dq.x = -dq.x; dq.y = -dq.y; dq.z = -dq.z; dq.w = -dq.w; }
            b->angular_velocity[0] = 2 * dq.x / h; b->angular_velocity[1] = 2 * dq.y / h; b->angular_velocity[2] = 2 * dq.z / h;
            bonepos[f][i] = x0[i]; bonerot[f][i] = r0[i];
        }
    }
    ymir_particle_proxy* fl = W->proxies + F * 19;
    memset(fl, 0, sizeof *fl);
    fl->shape = YMIR_PROXY_BOX; fl->size[0] = 50; fl->size[1] = 0.5f; fl->size[2] = 50; fl->position[1] = -0.5f; fl->rotation[3] = 1;
    W->body_count = F * 19 + 1;
}

static double now_ms(void)
{
    LARGE_INTEGER c, fq; QueryPerformanceCounter(&c); QueryPerformanceFrequency(&fq);
    return 1000.0 * (double)c.QuadPart / (double)fq.QuadPart;
}

int main(int argc, char** argv)
{
    F = argc > 1 ? atoi(argv[1]) : 1;
    float s = argc > 2 ? (float)atof(argv[2]) : 0.08f, sm = argc > 3 ? (float)atof(argv[3]) : 0.10f;
    int live = argc > 4 ? atoi(argv[4]) : 0, pinmode = argc > 5 ? atoi(argv[5]) : 0, filter = argc > 6 ? atoi(argv[6]) : 0;
    int gut = argc > 7 ? atoi(argv[7]) : 0, ticks = argc > 8 ? atoi(argv[8]) : 600, cut = argc > 9 ? atoi(argv[9]) : 0;
    humanoid();
    int cap = 4000 * F + 400;
    ymir_particle_capacities c = {cap, cap * 6, cap * 4, cap * 2, cap * 2, 4, 0, 0, 0};
    W = ymir_particles_create(&c, F * 19 + 1);
    PI = calloc(cap, sizeof *PI); pmass = calloc(cap, sizeof *pmass); PH = calloc(cap, sizeof *PH);
    DH = calloc(cap * 6, sizeof *DH); Dlayer = calloc(cap * 6, sizeof(int)); Dpart = calloc(cap * 6, sizeof(int)); Dfella = calloc(cap * 6, sizeof(int));
    uint8_t* pinned = calloc(cap, 1);
    int32_t* bgroup = calloc(F * 19 + 1, sizeof(int32_t));
    for (int f = 0; f < F; f++) for (int i = 0; i < 19; i++) bgroup[f * 19 + i] = f + 1;
    bgroup[F * 19] = -1;
    fill_proxies(0, 1.f / 480);
    double build0 = now_ms();
    v3* tmp = malloc(sizeof(v3) * 4000);
    int per_layer[2] = {0, 0};
    for (int f = 0; f < F; f++)
    {
        int first[2][19], cnt[2][19];
        for (int layer = 0; layer < 2; layer++)
        {
            float sp = layer == 0 ? s : sm, d = layer == 0 ? 0 : 0.012f;
            float mass = layer == 0 ? 0.012f * (sp * sp / 0.0064f) : 0.05f, rad = 0.45f * sp;
            for (int i = 0; i < 19; i++)
            {
                first[layer][i] = nparticles; cnt[layer][i] = 0;
                if (layer == 1 && (P[i].shape == BOX && P[i].size.x < 0.02f)) continue; // no muscle sheet in palm/fingers
                if (layer == 1 && P[i].size.x < 0.02f) continue;
                int n = sample(&P[i], sp, d, tmp);
                for (int k = 0; k < n; k++)
                {
                    v3 w = add(P[i].pos, tmp[k]);
                    int inside = 0;
                    for (int o = 0; o < 19; o++) if (o != i && sdf(&P[o], sub(w, P[o].pos)) < -0.25f * sp) { inside = 1; break; }
                    if (inside) continue;
                    v3 wp = add(bonepos[f][i], qrot(bonerot[f][i], tmp[k]));
                    int live_p = live == 3 || (live == 2 && f == 0) || (live == 1 && i == LOWERARM_L);
                    if (!live_p && getenv("ABSENT")) continue;
                    ymir_particle_desc pd = {{wp.x, wp.y, wp.z}, {w.x + 1.2f * f, w.y, w.z}, {0, 0, 0}, mass, rad, (uint32_t)(f + 1), 0};
                    pd.mass = live_p ? mass : 0.0f;
                    if (ymir_particles_create_particles(W, 1, &pd, stepc, &PH[nparticles]) != 0) { printf("create failed\n"); return 1; }
                    PI[nparticles] = (pinfo){f, i, layer, tmp[k]};
                    pmass[nparticles] = mass;
                    pinned[PH[nparticles].lane] = !live_p;
                    nparticles++; cnt[layer][i]++; if (f == 0) per_layer[layer]++;
                }
            }
        }
        if (f == 0 && getenv("PARTS")) { for (int i = 0; i < 19; i++) printf("part %d skin %d muscle %d;", i, cnt[0][i], cnt[1][i]); }
        // edges within a part and layer: 6 nearest within 1.6 spacing; bends over near-opposite pairs
        for (int layer = 0; layer < 2; layer++)
        {
            float sp = layer == 0 ? s : sm;
            for (int i = 0; i < 19; i++)
            {
                int a0 = first[layer][i], n = cnt[layer][i];
                int* nb = calloc((size_t)n * 6, sizeof(int)); int* nbe = calloc((size_t)n * 6, sizeof(int)); int* nn = calloc(n, sizeof(int));
                for (int a = 0; a < n; a++)
                {
                    int best[6]; float bd[6]; int nb6 = 0;
                    for (int b = 0; b < n; b++)
                    {
                        if (b == a) continue;
                        float dd = len(sub(PI[a0 + a].local, PI[a0 + b].local));
                        if (dd > 1.6f * sp) continue;
                        int q;
                        if (nb6 < 6) q = nb6++;
                        else { if (bd[5] <= dd) continue; q = 5; }
                        best[q] = b; bd[q] = dd;
                        for (int z = q; z > 0 && bd[z] < bd[z - 1]; z--) { float t = bd[z]; bd[z] = bd[z - 1]; bd[z - 1] = t; int u = best[z]; best[z] = best[z - 1]; best[z - 1] = u; }
                    }
                    for (int q = 0; q < nb6; q++)
                    {
                        int b = best[q];
                        if (b < a) { int seen = 0; for (int z = 0; z < nn[b]; z++) if (nb[b * 6 + z] == a) seen = 1; if (seen) continue; }
                        ymir_distance_desc dd = {PH[a0 + a], PH[a0 + b], -1.0f, layer == 0 ? 2e-4f : 1e-4f};
                        { int rc = ymir_particles_create_distances(W, 1, &dd, stepc, &DH[ndist]); if (rc) { printf("dist failed rc %d layer %d part %d a %d b %d n %d lanes %d %d\n", rc, layer, i, a, b, n, PH[a0+a].lane, PH[a0+b].lane); return 1; } }
                        Dlayer[ndist] = layer; Dpart[ndist] = i; Dfella[ndist] = f;
                        if (nn[a] < 6) { nb[a * 6 + nn[a]] = b; nbe[a * 6 + nn[a]] = ndist; nn[a]++; }
                        if (nn[b] < 6) { nb[b * 6 + nn[b]] = a; nbe[b * 6 + nn[b]] = ndist; nn[b]++; }
                        ndist++;
                    }
                }
                for (int b = 0; b < n; b++)
                    for (int x = 0; x < nn[b]; x++)
                        for (int y = x + 1; y < nn[b]; y++)
                        {
                            v3 u = sub(PI[a0 + nb[b * 6 + x]].local, PI[a0 + b].local), v = sub(PI[a0 + nb[b * 6 + y]].local, PI[a0 + b].local);
                            if (dot(u, v) / (len(u) * len(v)) > -0.85f) continue;
                            ymir_bend_desc bd = {DH[nbe[b * 6 + x]], DH[nbe[b * 6 + y]], 1e-3f, -1.0f};
                            ymir_handle hb;
                            if (ymir_particles_create_bends(W, 1, &bd, stepc, &hb) == 0) nbend++;
                        }
                free(nb); free(nbe); free(nn);
            }
        }
        // stitches across each joint (skin and muscle): child particles within 1.5 sp of the anchor to the nearest parent particle within 2 sp
        for (int layer = 0; layer < 2; layer++)
        {
            float sp = layer == 0 ? s : sm;
            for (int j = 0; j < nj; j++)
            {
                int pa = J[j].parent, ch = J[j].child;
                for (int a = first[layer][ch]; a < first[layer][ch] + cnt[layer][ch]; a++)
                {
                    v3 wa = add(P[ch].pos, PI[a].local);
                    if (len(sub(wa, J[j].anchor)) > 1.5f * sp + P[ch].size.x) continue;
                    int best = -1; float bd = 2.0f * sp;
                    for (int b = first[layer][pa]; b < first[layer][pa] + cnt[layer][pa]; b++)
                    {
                        float dd = len(sub(wa, add(P[pa].pos, PI[b].local)));
                        if (dd < bd) { bd = dd; best = b; }
                    }
                    if (best < 0) continue;
                    ymir_distance_desc dd = {PH[a], PH[best], -1.0f, 2e-4f};
                    if (ymir_particles_create_distances(W, 1, &dd, stepc, &DH[ndist]) != 0) { printf("stitch failed\n"); return 1; }
                    Dlayer[ndist] = 2 + layer; Dpart[ndist] = ch; Dfella[ndist] = f; ndist++; nstitch++;
                }
            }
        }
        // skin to nearest muscle of the same part
        for (int i = 0; i < 19; i++)
            for (int a = first[0][i]; a < first[0][i] + cnt[0][i]; a++)
            {
                int best = -1; float bd = 1e9f;
                for (int b = first[1][i]; b < first[1][i] + cnt[1][i]; b++) { float dd = len(sub(PI[a].local, PI[b].local)); if (dd < bd) { bd = dd; best = b; } }
                if (best < 0) continue;
                ymir_distance_desc dd = {PH[a], PH[best], -1.0f, 5e-4f};
                if (ymir_particles_create_distances(W, 1, &dd, stepc, &DH[ndist]) != 0) { printf("sm failed\n"); return 1; }
                Dlayer[ndist] = 4; Dpart[ndist] = i; Dfella[ndist] = f; ndist++;
            }
    }
    // attachments: every flesh particle to its bone point
    for (int k = 0; k < nparticles; k++)
    {
        ymir_attachment_desc ad = {YMIR_ATTACH_BODY, 0, PH[k], PI[k].fella * 19 + PI[k].part, {PI[k].local.x, PI[k].local.y, PI[k].local.z}, PI[k].layer == 0 ? 1e-3f : 1e-4f, 0};
        ymir_handle ha;
        if (ymir_particles_attach(W, &ad, W->body_count, stepc, &ha) != 0) { printf("attach failed\n"); return 1; }
        natt++;
    }
    int flesh = nparticles;
    double build_ms = now_ms() - build0;
    // gut: a released real-length rope of 300 particles hanging from the pelvis of Fella 0
    int gut0 = nparticles, ngut = gut ? 300 : 0;
    ymir_handle* GE = calloc(400, sizeof *GE);
    for (int k = 0; k < ngut; k++)
    {
        float a = k * 0.18f, r = 0.05f + 0.0012f * k;
        v3 x = V(r * cosf(a), 0.9f, 0.25f + r * sinf(a));
        ymir_particle_desc pd = {{x.x, x.y, x.z}, {0.0267f * k, 0, 0}, {0, 0, 0}, 0.0267f * 0.0003f * 1050.0f / 0.0267f * 0.0267f, 0.0125f, 99u | YMIR_PHASE_SELF_COLLIDE | YMIR_PHASE_SELF_FILTER, 0};
        pd.mass = 0.008f;
        ymir_particles_create_particles(W, 1, &pd, stepc, &PH[nparticles]);
        PI[nparticles] = (pinfo){0, -1, 9, x}; nparticles++;
    }
    int ngutd = 0;
    for (int k = 1; k < ngut; k++)
    {
        ymir_distance_desc dd = {PH[gut0 + k - 1], PH[gut0 + k], 0.0267f, 0};
        ymir_particles_create_distances(W, 1, &dd, stepc, &GE[k - 1]); ngutd++;
    }
    for (int k = 1; k + 1 < ngut; k++) { ymir_bend_desc bd = {GE[k - 1], GE[k], 1e-2f, -1}; ymir_handle hb; ymir_particles_create_bends(W, 1, &bd, stepc, &hb); }
    if (ngut)
    {
        for (int e = 0; e < 2; e++)
        {
            int k = e ? ngut - 1 : 0;
            ymir_attachment_desc ad = {YMIR_ATTACH_BODY, YMIR_ATTACH_FLAG_TETHER, PH[gut0 + k], 0, {0.02f * (e ? 1 : -1), -0.02f, 0.09f}, 0, 0};
            ymir_handle ha; ymir_particles_attach(W, &ad, W->body_count, stepc, &ha);
        }
    }
    if (pinmode) probe_pinned = pinned;
    if (getenv("NOCONTACT")) probe_nocontact = 1;
    if (filter) probe_body_group = bgroup;
    if (filter && ngut) bgroup[0] = 0; // the gut (group 99) collides with every bone; nothing to do
    // per-Fella counts
    int fd = 0, fs = 0;
    for (int e = 0; e < ndist; e++) if (Dfella[e] == 0) { fd++; if (Dlayer[e] >= 2 && Dlayer[e] <= 3) fs++; }
    float h = 1.0f / 480.0f;
    double* tk = malloc(sizeof(double) * (ticks + 240));
    float t = 0;
    int warm = 120;
    double cut_ms = -1, sever_ms = -1, wake_ms = -1;
    float maxdev = 0;
    for (int n = 0; n < warm + ticks; n++)
    {
        // pinned particles are written from their bone at the tick's start (Ymir's pin write, one per tick)
        double t0 = now_ms();
        fill_proxies(t, h);
        for (int k = 0; k < flesh; k++)
        {
            int l = PH[k].lane;
            if (!pinned[l]) continue;
            v3 w = add(bonepos[PI[k].fella][PI[k].part], qrot(bonerot[PI[k].fella][PI[k].part], PI[k].local));
            W->pos_x[l] = w.x; W->pos_y[l] = w.y; W->pos_z[l] = w.z;
        }
        ymir_particles_begin(W);
        for (int sstep = 0; sstep < 4; sstep++)
        {
            if (sstep) fill_proxies(t + sstep * h, h);
            ymir_particles_substep(W, h, 0, -9.81f, 0);
        }
        ymir_particles_end(W, 4);
        double t1 = now_ms();
        t += 4 * h;
        stepc++;
        if (n >= warm) tk[n - warm] = t1 - t0;
        if (cut && n == warm + ticks / 2)
        {
            // wound: remove the skin edges of Fella 0's left forearm within 6 cm of its front point; then sever at the left elbow (stitches)
            ymir_handle hs[64]; int m = 0;
            for (int e = 0; e < ndist && m < 64; e++)
                if (Dfella[e] == 0 && Dpart[e] == LOWERARM_L && Dlayer[e] == 0)
                {
                    int a = W->dist_a[DH[e].lane];
                    v3 x = V(W->pos_x[a], W->pos_y[a], W->pos_z[a]);
                    v3 c0 = add(bonepos[0][LOWERARM_L], qrot(bonerot[0][LOWERARM_L], V(0, 0, .04f)));
                    if (len(sub(x, c0)) < 0.06f) hs[m++] = DH[e];
                }
            double c0 = now_ms();
            ymir_particles_remove(W, YMIR_LANES_DISTANCE, m, hs, stepc);
            cut_ms = now_ms() - c0;
            printf("cut: %d skin edges\n", m);
            m = 0;
            for (int e = 0; e < ndist && m < 64; e++) if (Dfella[e] == 0 && Dpart[e] == LOWERARM_L && (Dlayer[e] == 2 || Dlayer[e] == 3)) hs[m++] = DH[e];
            c0 = now_ms();
            ymir_particles_remove(W, YMIR_LANES_DISTANCE, m, hs, stepc);
            sever_ms = now_ms() - c0;
            printf("sever: %d stitches; forearm skin component %d, upper arm skin component %d\n", m,
                   W->component[PH[0].lane], 0);
            // wake Fella 0's thigh flesh: pinned -> live (mass back, velocity of the bone point)
            c0 = now_ms();
            for (int k = 0; k < flesh; k++)
                if (PI[k].fella == 0 && PI[k].part == 7)
                {
                    int l = PH[k].lane;
                    if (!pinned[l]) continue;
                    pinned[l] = 0; W->inv_mass[l] = 1.0f / pmass[k];
                }
            wake_ms = now_ms() - c0;
        }
        if (n == warm + ticks - 1)
            for (int k = 0; k < flesh; k++)
            {
                int l = PH[k].lane;
                if (pinned[l]) continue;
                v3 w = add(bonepos[PI[k].fella][PI[k].part], qrot(bonerot[PI[k].fella][PI[k].part], PI[k].local));
                float dv = len(sub(V(W->pos_x[l], W->pos_y[l], W->pos_z[l]), w));
                if (dv > maxdev) maxdev = dv;
            }
    }
    // median and mean
    int nt = ticks;
    for (int a = 1; a < nt; a++) { double v = tk[a]; int b = a - 1; while (b >= 0 && tk[b] > v) { tk[b + 1] = tk[b]; b--; } tk[b + 1] = v; }
    double mean = 0; for (int a = 0; a < nt; a++) mean += tk[a]; mean /= nt;
    int livec = 0; for (int k = 0; k < nparticles; k++) livec += !pinned[PH[k].lane] || k >= flesh;
    printf("FELLA F=%d skin=%.3f muscle=%.3f live=%d pinmode=%d filter=%d gut=%d | per Fella: skin %d muscle %d particles, %d distances (%d stitches), bends total %d, attachments total %d | live particles %d | ms/tick median %.3f mean %.3f p95 %.3f max %.3f | max live deviation %.3f m",
           F, s, sm, live, pinmode, filter, gut, per_layer[0], per_layer[1], fd, fs, nbend, natt, livec, tk[nt / 2], mean, tk[(int)(nt * 0.95)], tk[nt - 1], maxdev);
    printf(" | phases ms/tick grid %.3f pcontact %.3f att %.3f dist %.3f bend %.3f tether %.3f body %.3f", probe_t[0]/(warm+ticks), probe_t[1]/(warm+ticks), probe_t[2]/(warm+ticks), probe_t[3]/(warm+ticks), probe_t[4]/(warm+ticks), probe_t[5]/(warm+ticks), probe_t[6]/(warm+ticks));
    if (cut) printf(" | cut %.3f ms sever %.3f ms wake %.4f ms", cut_ms, sever_ms, wake_ms);
    printf("\n");
    printf("BUILD %.1f ms;", build_ms); return 0;
}

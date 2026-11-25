// Sieve probe: a skin sheet attached along its length to a horizontal static capsule (a BDS thigh: r 0.07, half 0.18) at spacing s,
// under a fluid pour (7x7x7, r 0.0125, 15.6 g) and a dropped solid block (4x4x4 solid particles, r 0.02) at 4 substeps, 60 Hz as the
// Ymir pour test and 120 Hz as BDS. Readings: worst edge stretch, deep overlaps (pairs within half a contact distance), fluid and
// solid particles that reach the bone surface through the skin (centre within r_cap + 1.5 r of the axis segment), and fallen below.
// usage: sieve s hz substeps attach_compliance dropped(0 fluid,1 solid)
#include "ymir_particles.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static float seglen(float x, float y, float z, float half) // distance from the capsule axis segment (along x here)
{
    float cx = x < -half ? -half : x > half ? half : x;
    return sqrtf((x - cx) * (x - cx) + y * y + z * z);
}

int main(int argc, char** argv)
{
    float s = argc > 1 ? (float)atof(argv[1]) : 0.08f;
    float hz = argc > 2 ? (float)atof(argv[2]) : 120.f;
    int sub = argc > 3 ? atoi(argv[3]) : 4;
    float ac = argc > 4 ? (float)atof(argv[4]) : 1e-3f;
    int solid = argc > 5 ? atoi(argv[5]) : 0;
    const float R = 0.07f, HALF = 0.18f, Y = 0.5f;
    ymir_particle_capacities c = {2048, 8192, 8192, 1024, 2048, 2, 512, 0, 0};
    ymir_particles* W = ymir_particles_create(&c, 2);
    ymir_material_desc m = {0.3f, 0.01f, 0, 0, 1};
    ymir_particles_set_material(W, 0, &m);
    // the capsule lies along x: Ymir capsules run along local y, so rotate y onto x (90 degrees about z)
    float q[4] = {0, 0, sinf(-0.785398f), cosf(-0.785398f)};
    ymir_particle_proxy* b = W->proxies;
    memset(b, 0, 2 * sizeof *b);
    b[0].shape = YMIR_PROXY_CAPSULE; b[0].size[0] = R; b[0].size[1] = HALF; b[0].position[1] = Y; memcpy(b[0].rotation, q, sizeof q);
    b[1].shape = YMIR_PROXY_BOX; b[1].size[0] = 5; b[1].size[1] = 0.5f; b[1].size[2] = 5; b[1].position[1] = -0.5f; b[1].rotation[3] = 1;
    W->body_count = 2;
    // skin: rings along the capsule at spacing s, staggered, centres on the bone surface; caps left open (a limb continues at its joints)
    int rings = (int)ceilf(2 * HALF / s), per = (int)fmaxf(6, roundf(2 * 3.14159f * R / s));
    int n = (rings + 1) * per;
    ymir_handle* ph = calloc(n, sizeof *ph);
    float* lx = calloc(n, sizeof(float)); float* ly = calloc(n, sizeof(float)); float* lz = calloc(n, sizeof(float));
    for (int i = 0, k = 0; i <= rings; i++)
        for (int j = 0; j < per; j++, k++)
        {
            float a = 2 * 3.14159f * (j + 0.5f * (i & 1)) / per;
            float x = -HALF + 2 * HALF * i / rings;
            lx[k] = x; ly[k] = R * cosf(a); lz[k] = R * sinf(a);
            // local frame of the body is y along the axis: local = (-world y, world x, z) rotated; keep world-space rest and compute the body point
            ymir_particle_desc d = {{x, Y + ly[k], lz[k]}, {x, Y + ly[k], lz[k]}, {0, 0, 0}, 0.012f * (s * s / 0.0064f), 0.45f * s, 1u, 0};
            ymir_particles_create_particles(W, 1, &d, 1, &ph[k]);
        }
    // edges: ring neighbours, next ring's two nearest; bends along rings and along the axis
    ymir_handle* eh = calloc(n * 4, sizeof *eh);
    int ne = 0;
    int* ringe = calloc(n, sizeof(int)); int* axe = calloc(n, sizeof(int));
    for (int i = 0; i <= rings; i++)
        for (int j = 0; j < per; j++)
        {
            int k = i * per + j;
            ymir_distance_desc d = {ph[k], ph[i * per + (j + 1) % per], -1, 2e-4f};
            ymir_particles_create_distances(W, 1, &d, 1, &eh[ne]); ringe[k] = ne++;
            if (i < rings)
            {
                int j2 = (i & 1) ? (j + 1) % per : j, j3 = (i & 1) ? j : (j + per - 1) % per;
                ymir_distance_desc d2 = {ph[k], ph[(i + 1) * per + j2], -1, 2e-4f}, d3 = {ph[k], ph[(i + 1) * per + j3], -1, 2e-4f};
                ymir_particles_create_distances(W, 1, &d2, 1, &eh[ne]); axe[k] = ne++;
                ymir_particles_create_distances(W, 1, &d3, 1, &eh[ne]); ne++;
            }
        }
    for (int i = 0; i <= rings; i++)
        for (int j = 0; j < per; j++)
        {
            int k = i * per + j, kp = i * per + (j + per - 1) % per;
            ymir_bend_desc bd = {eh[ringe[kp]], eh[ringe[k]], 1e-3f, -1};
            ymir_handle hb; ymir_particles_create_bends(W, 1, &bd, 1, &hb);
        }
    for (int k = 0; k < n; k++)
    {
        // the body frame: rotation q maps local y onto world x; local = q^-1 (world - centre): (x, y', z) world -> local (y', -x ... ) computed numerically
        float wx = lx[k], wy = ly[k], wz = lz[k];
        // inverse of a -90 degree turn about z: local x = -wy? Use the exact inverse: rotate by +90 about z: (x, y) -> (-y, x)
        float px = -wy, py = wx, pz = wz;
        (void)px;
        ymir_attachment_desc ad = {YMIR_ATTACH_BODY, 0, ph[k], 0, {-wy * -1.0f * 0 + (-wy) * 0 + wy * 0 + (-wy) , py, pz}, ac, 0};
        ad.point[0] = -wy; ad.point[1] = wx; ad.point[2] = wz;
        // check: q (y-axis -> x-axis) applied to (px, py, pz) must give (wx, wy, wz)
        ad.point[0] = -wy; ad.point[1] = wx; ad.point[2] = wz;
        ymir_handle ha; ymir_particles_attach(W, &ad, 2, 1, &ha);
    }
    // the load: a 7x7x7 block 0.25 m above the capsule's top, centred on it
    int nl = solid ? 64 : 343, side = solid ? 4 : 7;
    float lr = solid ? 0.02f : 0.0125f, sp = 2 * lr;
    ymir_handle* lh = calloc(nl, sizeof *lh);
    for (int a = 0; a < side; a++) for (int bb = 0; bb < side; bb++) for (int cc = 0; cc < side; cc++)
    {
        int k = (a * side + bb) * side + cc;
        float x = -0.5f * (side - 1) * sp + a * sp, y = Y + R + 0.25f + bb * sp, z = -0.5f * (side - 1) * sp + cc * sp;
        ymir_particle_desc d = {{x, y, z}, {x, y, z}, {0, 0, 0}, solid ? 0.06f : 0.015625f, lr, solid ? (6u | YMIR_PHASE_SELF_COLLIDE) : (6u | YMIR_PHASE_FLUID), 0};
        ymir_particles_create_particles(W, 1, &d, 1, &lh[k]);
    }
    float h = 1.0f / hz / sub, worst = 0;
    int deep = 0, through = 0, inside = 0, ticks = (int)(3 * hz);
    char* hit = calloc(nl, 1);
    for (int t = 0; t < ticks; t++)
    {
        ymir_particles_begin(W);
        for (int ss = 0; ss < sub; ss++)
        {
            for (int k = 0; k < 2; k++) { /* static: pose fixed, velocities zero */ }
            ymir_particles_substep(W, h, 0, -9.81f, 0);
            b[0].position[1] = Y; b[0].position[0] = 0; b[0].position[2] = 0; memcpy(b[0].rotation, q, sizeof q);
            b[1].position[1] = -0.5f; b[1].position[0] = b[1].position[2] = 0; b[1].rotation[0] = b[1].rotation[1] = b[1].rotation[2] = 0; b[1].rotation[3] = 1;
        }
        ymir_particles_end(W, sub);
        for (int e = 0; e < ne; e++)
        {
            int i = W->dist_a[eh[e].lane], j = W->dist_b[eh[e].lane];
            float dx = W->pos_x[i] - W->pos_x[j], dy = W->pos_y[i] - W->pos_y[j], dz = W->pos_z[i] - W->pos_z[j];
            float st = sqrtf(dx * dx + dy * dy + dz * dz) / W->dist_rest[eh[e].lane] - 1;
            if (st > worst) worst = st;
        }
        for (int k = 0; k < nl; k++)
        {
            int l = lh[k].lane;
            for (int s2 = 0; s2 < n; s2++)
            {
                int i = ph[s2].lane;
                float dx = W->pos_x[i] - W->pos_x[l], dy = W->pos_y[i] - W->pos_y[l], dz = W->pos_z[i] - W->pos_z[l];
                if (sqrtf(dx * dx + dy * dy + dz * dz) < 0.5f * (W->radius[i] + lr)) deep++;
            }
            if (fabsf(W->pos_x[l]) < HALF && seglen(W->pos_x[l], W->pos_y[l] - Y, W->pos_z[l], HALF) < R) inside++;
            if (!hit[k] && fabsf(W->pos_x[l]) < HALF && seglen(W->pos_x[l], W->pos_y[l] - Y, W->pos_z[l], HALF) < R + 1.5f * lr) { hit[k] = 1; through++; }
        }
    }
    // a sanity read of the skin's place: mean distance of skin centres from the axis (R when held)
    float md = 0;
    for (int k = 0; k < n; k++) { int i = ph[k].lane; md += seglen(W->pos_x[i], W->pos_y[i] - Y, W->pos_z[i], HALF); }
    int below = 0; for (int k = 0; k < nl; k++) below += W->pos_y[lh[k].lane] < 0.05f;
    printf("SIEVE s=%.3f %gHz x%d attach_c=%g load=%s: skin %d particles r %.3f, %d edges | worst stretch %.4f | deep (tick x pair) %d | load reaching the bone through the skin %d of %d | inside the bone (tick x particle) %d | on the floor at 3 s %d | skin mean axis distance %.4f (R %.3f)\n",
           s, hz, sub, ac, solid ? "solid" : "fluid", n, 0.45f * s, ne, worst, deep, through, nl, inside, below, md / n, R);
    return 0;
}

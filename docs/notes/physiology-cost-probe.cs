// Cost probe for the physiology slow step (Imagination pass bds-physiology-map). Pessimistic: exp() every pair, every step.
using System;
using System.Diagnostics;

const int C = 128;   // tissue compartments (parts x layers, organs included)
const int S = 16;    // active substance slots
const int E = 64;    // active effects (PD curves)
const int Props = 24; // properties per compartment, effective value recomputed every step
const int Parts = 32;
const double dt = 0.1; // 10 Hz slow step

var rng = new Random(1);
double[] V = new double[C], F = new double[C], water = new double[C], temp = new double[Parts];
double[] Q = new double[C * S], P = new double[C * S], osm = new double[S], clear = new double[S];
double[] blood = new double[S];
double[] baseP = new double[C * Props], factor = new double[C * Props], offset = new double[C * Props], eff = new double[C * Props];
int[] effSub = new int[E], effComp = new int[E], effProp = new int[E];
double[] ec50 = new double[E], emax = new double[E];
for (int c = 0; c < C; c++) { V[c] = 0.1 + rng.NextDouble(); F[c] = 0.01 + rng.NextDouble() * 0.1; water[c] = V[c] * 0.7; }
for (int i = 0; i < C * S; i++) { P[i] = 0.5 + rng.NextDouble() * 4; Q[i] = rng.NextDouble(); }
for (int s = 0; s < S; s++) { osm[s] = rng.NextDouble(); clear[s] = rng.NextDouble() * 0.01; blood[s] = rng.NextDouble(); }
for (int i = 0; i < C * Props; i++) baseP[i] = 1 + rng.NextDouble();
for (int e = 0; e < E; e++) { effSub[e] = rng.Next(S); effComp[e] = rng.Next(C); effProp[e] = rng.Next(Props); ec50[e] = 0.5; emax[e] = 2; }
double bloodWater = 5.0, sink = 0;

void Step()
{
    Span<double> venous = stackalloc double[S];
    venous.Clear();
    double bloodOsm = 0;
    for (int s = 0; s < S; s++) bloodOsm += blood[s] * osm[s];
    bloodOsm /= bloodWater;
    for (int c = 0; c < C; c++)
    {
        double cOsm = 0;
        for (int s = 0; s < S; s++)
        {
            int i = c * S + s;
            double ceq = blood[s] / bloodWater * P[i] * V[c];     // equilibrium amount
            double k = F[c] / (P[i] * V[c]);
            double q1 = ceq + (Q[i] - ceq) * Math.Exp(-k * dt);   // exact for fixed arterial concentration
            venous[s] -= q1 - Q[i];
            Q[i] = q1;
            cOsm += q1 * osm[s];
        }
        cOsm /= water[c];
        double jw = 0.001 * (cOsm - bloodOsm) * dt;              // osmotic water flux
        water[c] += jw; bloodWater -= jw;
    }
    for (int s = 0; s < S; s++) blood[s] = Math.Max(0, blood[s] + venous[s] - clear[s] * blood[s] * dt);
    for (int i = 0; i < C * Props; i++) { factor[i] = 1; offset[i] = 0; }
    for (int e = 0; e < E; e++)
    {
        double conc = Q[effComp[e] * S + effSub[e]] / water[effComp[e]];
        double h = emax[e] * conc * conc / (ec50[e] * ec50[e] + conc * conc);
        factor[effComp[e] * Props + effProp[e]] *= 1 + h;
    }
    for (int i = 0; i < C * Props; i++) eff[i] = baseP[i] * factor[i] + offset[i];
    for (int p = 1; p < Parts; p++) { double h = 0.05 * (temp[p - 1] - temp[p]) * dt; temp[p] += h; temp[p - 1] -= h; }
    sink += eff[7] + Q[3];
}

for (int i = 0; i < 2000; i++) Step();
var sw = Stopwatch.StartNew();
const int N = 20000;
for (int i = 0; i < N; i++) Step();
sw.Stop();
Console.WriteLine($"C={C} S={S} E={E} props={C * Props}: {sw.Elapsed.TotalMilliseconds * 1000 / N:F2} us per slow step (sink {sink:G3})");
Console.WriteLine($"Allocated during run: {GC.GetAllocatedBytesForCurrentThread()} bytes total (setup included)");

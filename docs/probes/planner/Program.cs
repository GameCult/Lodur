using System; using System.Diagnostics; using System.Collections.Generic;
// Facts: 128 bits. Self: Near(s)=s (0..15), Holding(s)=16+s, HandFree=32, Free=33, Fed=34.
// Per slot: Unlocked(s)=64+s, Open(s)=80+s, Off(s)=96+s.
struct F { public ulong A, B; 
  public static F Bit(int i){ var f=new F(); if(i<64) f.A=1UL<<i; else f.B=1UL<<(i-64); return f; }
  public static F operator|(F x,F y)=>new F{A=x.A|y.A,B=x.B|y.B};
  public static F operator&(F x,F y)=>new F{A=x.A&y.A,B=x.B&y.B};
  public static F operator~(F x)=>new F{A=~x.A,B=~x.B};
  public bool Any=>(A|B)!=0; }
struct Act { public F Pre, Add, Del; public float Cost; }
static class P {
  const int N=16; static Act[] acts; static int nActs; static F start;
  // per goal: want fact per target slot
  static int[] doors={0,1}, keys={2,3}; const int knife=4, strap=5, sw=6, food=7;
  static int HB(int s)=>16+s;
  static void Build(){
    var l=new List<Act>(); F nearAll=new F{A=0xFFFF};
    for(int s=0;s<N;s++){ if(s==strap) continue; l.Add(new Act{Pre=F.Bit(33),Add=F.Bit(s),Del=nearAll&~F.Bit(s),Cost=2}); }
    foreach(int s in new[]{2,3,knife,food}){ l.Add(new Act{Pre=F.Bit(s)|F.Bit(32),Add=F.Bit(HB(s)),Del=F.Bit(32),Cost=1});
      l.Add(new Act{Pre=F.Bit(HB(s)),Add=F.Bit(32),Del=F.Bit(HB(s)),Cost=1}); }
    foreach(int k in keys) foreach(int d in doors) l.Add(new Act{Pre=F.Bit(HB(k))|F.Bit(d),Add=F.Bit(64+d),Cost=1});
    foreach(int d in doors) l.Add(new Act{Pre=F.Bit(d)|F.Bit(64+d),Add=F.Bit(80+d),Cost=1});
    l.Add(new Act{Pre=F.Bit(HB(knife)),Add=F.Bit(33),Cost=2});           // cut strap
    l.Add(new Act{Pre=F.Bit(sw),Add=F.Bit(96+sw),Cost=1});               // switch off
    l.Add(new Act{Pre=F.Bit(HB(food)),Add=F.Bit(34),Cost=1});            // eat
    // distractor verbs on other things (poke, shove, look) that achieve per-slot facts nobody wants
    for(int s=8;s<N;s++){ l.Add(new Act{Pre=F.Bit(s),Add=F.Bit(96+s),Cost=1}); l.Add(new Act{Pre=F.Bit(s)|F.Bit(32),Add=F.Bit(80+s),Cost=1}); }
    acts=l.ToArray(); nActs=acts.Length;
    start=F.Bit(knife)|F.Bit(32); // restrained, knife in reach, hand free
  }
  static bool Sub(F r,F s)=>((r.A&~s.A)|(r.B&~s.B))==0;
  static long nodes;
  // regressive DFS: required set r, depth left d
  static bool Reg(F r,int d){ nodes++; if(Sub(r,start)) return true; if(d==0) return false;
    for(int i=0;i<nActs;i++){ ref Act a=ref acts[i]; if(!(a.Add&r).Any) continue; if((a.Del&r).Any) continue;
      var r2=(r&~a.Add)|a.Pre; if(Reg(r2,d-1)) return true; } return false; }
  static bool Fwd(F s,F want,int d){ nodes++; if(Sub(want,s)) return true; if(d==0) return false;
    for(int i=0;i<nActs;i++){ ref Act a=ref acts[i]; if(!Sub(a.Pre,s)) continue; if(Sub(a.Add,s)) continue;
      var s2=(s&~a.Del)|a.Add; if(Fwd(s2,want,d-1)) return true; } return false; }
  // planned wants: escape x2 doors (8 steps), stop pain (4), eat (5), plus 2 near ones: reach knife (1), and look-poke (2)
  static F[] wants;
  static int Plan(int depth,bool fwd){ int found=0; foreach(var w in wants){ for(int d=0; d<=depth; d++){ bool ok= fwd? Fwd(start,w,d): Reg(w,d); if(ok){found++;break;} } } return found; }
  static double sink;
  static void Utility(){ double best=0; for(int g=0;g<6;g++) for(int s=0;s<N;s++){ double x=(s*0.37+g)%1.0; double y=1-x*x; y*=Math.Pow(0.5+0.5*x,2); if(y>best) best=y; } sink+=best; }
  static void Main(string[] args){ Build(); wants=new[]{F.Bit(80+0),F.Bit(80+1),F.Bit(96+sw),F.Bit(34),F.Bit(HB(knife)),F.Bit(80+9)};
    Console.WriteLine($"grounded actions {nActs}");
    for(int d=1;d<=8;d++){ nodes=0; int f=Plan(d,false); long rn=nodes; nodes=0; int ff= d<=5? Plan(d,true):-1; Console.WriteLine($"depth {d}: plans found {f}/6, regressive nodes {rn}, forward nodes {(d<=5?nodes:-1)} found {ff}"); }
    // timing: paired interleaved medians, rounds x conditions in shuffled order
    var conds=new List<(string,Action)>{("utility-only",()=>Utility())};
    for(int d=1;d<=4;d++){int dd=d; conds.Add(($"reg d{d}",()=>{Utility();Plan(dd,false);})); }
    for(int d=1;d<=4;d++){int dd=d; conds.Add(($"fwd d{d}",()=>{Utility();Plan(dd,true);})); }
    int rounds=41, reps=200; var t=new double[conds.Count,rounds]; var rng=new Random(7); var sw2=new Stopwatch();
    for(int w=0;w<3;w++) foreach(var c in conds) for(int i=0;i<reps;i++) c.Item2();
    for(int r=0;r<rounds;r++){ var order=new int[conds.Count]; for(int i=0;i<order.Length;i++)order[i]=i; for(int i=order.Length-1;i>0;i--){int j=rng.Next(i+1);(order[i],order[j])=(order[j],order[i]);}
      foreach(int c in order){ sw2.Restart(); for(int i=0;i<reps;i++) conds[c].Item2(); sw2.Stop(); t[c,r]=sw2.Elapsed.TotalMilliseconds*1000.0/reps; } }
    double Med(double[] a){Array.Sort(a);return a[a.Length/2];}
    var b=new double[rounds]; for(int r=0;r<rounds;r++) b[r]=t[0,r];
    for(int c=0;c<conds.Count;c++){ var a=new double[rounds]; var dlt=new double[rounds]; for(int r=0;r<rounds;r++){a[r]=t[c,r]; dlt[r]=t[c,r]-t[0,r];}
      Console.WriteLine($"{conds[c].Item1,-14} median {Med(a),9:F2} us/Select   paired delta over utility {Med(dlt),9:F2} us"); }
    Console.WriteLine(sink>0?"":"."); } }

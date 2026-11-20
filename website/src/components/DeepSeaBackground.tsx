import { useMemo, type CSSProperties } from "react";

/** 決定的な疑似乱数（再描画で粒子配置がぶれないように） */
function seeded(seed: number) {
  let s = seed;
  return () => {
    s = (s * 16807) % 2147483647;
    return (s - 1) / 2147483646;
  };
}

const NOISE =
  "url(\"data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='140' height='140'><filter id='n'><feTurbulence baseFrequency='0.9'/></filter><rect width='100%' height='100%' filter='url(%23n)'/></svg>\")";

/** 深海の多層グラデーション＋コースティクス＋漂う光の粒子。すべて CSS アニメーション（GPU 合成のみ）。 */
export default function DeepSeaBackground() {
  const particles = useMemo(() => {
    const rnd = seeded(42);
    return Array.from({ length: 34 }, (_, i) => ({
      id: i,
      left: rnd() * 100,
      size: 1 + rnd() * 3,
      delay: -rnd() * 30,
      duration: 18 + rnd() * 26,
      opacity: 0.15 + rnd() * 0.45,
    }));
  }, []);

  return (
    <div aria-hidden className="pointer-events-none fixed inset-0 -z-10 overflow-hidden">
      <div className="absolute inset-0 bg-[radial-gradient(120%_80%_at_50%_-10%,#0B1F3F_0%,#061329_45%,#020814_100%)]" />
      {/* 水面から差し込む光条 */}
      <div
        className="absolute -top-40 left-1/2 h-[90vh] w-[140vw] -translate-x-1/2 opacity-70"
        style={{
          background:
            "conic-gradient(from 180deg at 50% 0%, transparent 40%, rgba(96,165,250,0.10) 46%, transparent 50%, rgba(34,211,238,0.07) 54%, transparent 60%)",
          maskImage: "linear-gradient(to bottom, black, transparent)",
          WebkitMaskImage: "linear-gradient(to bottom, black, transparent)",
        }}
      />
      {/* コースティクス */}
      <div className="absolute -left-1/4 top-[-10%] h-[70vh] w-[70vw] animate-caustic rounded-full bg-azure/10 blur-[120px]" />
      <div
        className="absolute -right-1/4 top-[30%] h-[60vh] w-[60vw] animate-caustic rounded-full bg-azure-cyan/[0.06] blur-[140px]"
        style={{ animationDelay: "-8s" }}
      />
      {/* 漂う光の粒子／泡 */}
      {particles.map((p) => (
        <span
          key={p.id}
          className="absolute bottom-[-10px] animate-rise rounded-full bg-[#9fd4ff] shadow-[0_0_8px_rgba(96,165,250,0.8)]"
          style={
            {
              left: `${p.left}%`,
              width: p.size,
              height: p.size,
              animationDelay: `${p.delay}s`,
              "--d": `${p.duration}s`,
              "--o": p.opacity,
            } as CSSProperties
          }
        />
      ))}
      {/* 粒状ノイズでグラデーションのバンディングを抑える */}
      <div className="absolute inset-0 opacity-[0.035] mix-blend-overlay" style={{ backgroundImage: NOISE }} />
    </div>
  );
}

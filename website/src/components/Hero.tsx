import { useRef } from "react";
import { motion, useScroll, useTransform } from "framer-motion";
import { ArrowDown, Archive, Download, ShieldCheck } from "lucide-react";
import TableMockup from "./TableMockup";
import { useDownload } from "./DownloadContext";
import { DOWNLOADS, PRODUCT } from "../data/product";

const ease = [0.16, 1, 0.3, 1] as const;

export default function Hero() {
  const { startDownload } = useDownload();
  const ref = useRef<HTMLElement>(null);
  const { scrollYProgress } = useScroll({ target: ref, offset: ["start start", "end start"] });
  const copyY = useTransform(scrollYProgress, [0, 1], [0, -80]);
  const copyOpacity = useTransform(scrollYProgress, [0, 0.55], [1, 0]);
  const tableY = useTransform(scrollYProgress, [0, 1], [0, 120]);
  const tableRotate = useTransform(scrollYProgress, [0, 1], [14, 0]);
  const tableScale = useTransform(scrollYProgress, [0, 1], [1, 0.94]);

  return (
    <section id="top" ref={ref} className="relative overflow-hidden pb-24 pt-32 sm:pt-40">
      <motion.div style={{ y: copyY, opacity: copyOpacity }} className="relative mx-auto max-w-5xl px-4 text-center sm:px-6">
        <motion.a
          href="#download"
          initial={{ opacity: 0, y: 12 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 0.8, ease }}
          className="glass inline-flex items-center gap-2 rounded-full px-4 py-1.5 text-xs font-medium text-ice-body transition hover:border-azure-soft/40"
        >
          <span className="relative flex h-2 w-2">
            <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-azure-cyan opacity-60" />
            <span className="relative inline-flex h-2 w-2 rounded-full bg-azure-cyan" />
          </span>
          Windows 10 / 11 • {PRODUCT.version} リリース
        </motion.a>

        <motion.h1
          initial={{ opacity: 0, y: 24 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 1.1, delay: 0.1, ease }}
          className="text-gradient mt-8 text-balance text-[clamp(30px,9.4vw,44px)] font-extrabold leading-[1.08] tracking-tightest sm:text-7xl lg:text-[88px]"
        >
          静寂の卓上に、
          <br />
          極上の駆け引きを。
        </motion.h1>

        <motion.p
          initial={{ opacity: 0, y: 20 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 1, delay: 0.25, ease }}
          className="mx-auto mt-7 max-w-2xl text-pretty text-base leading-relaxed text-ice-body sm:text-lg"
        >
          約60のローカルルールと近距離オフライン対戦を、
          <span className="inline-block">ひとつの美しい卓に。</span>
          <span className="block">
            Windows のための<span className="inline-block">&nbsp;4 人対戦大富豪、</span>
            <span className="inline-block font-semibold text-ice">DTTS。</span>
          </span>
        </motion.p>

        <motion.div
          initial={{ opacity: 0, y: 20 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 1, delay: 0.4, ease }}
          className="mt-10 flex flex-col items-center justify-center gap-3 sm:flex-row"
        >
          <button onClick={() => startDownload("installer")} className="btn-primary w-full sm:w-auto">
            <Download className="h-4 w-4" />
            無料ダウンロード ({DOWNLOADS.installer.fileName})
          </button>
          <button onClick={() => startDownload("zip")} className="btn-ghost w-full sm:w-auto">
            <Archive className="h-4 w-4" />
            ZIP 版を入手 ({DOWNLOADS.zip.fileName})
          </button>
        </motion.div>
        <motion.p
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          transition={{ delay: 0.7 }}
          className="mx-auto mt-4 flex max-w-xs items-start justify-center gap-1.5 text-left text-xs text-ice-mute sm:max-w-none sm:items-center"
        >
          <ShieldCheck className="mt-px h-3.5 w-3.5 shrink-0 text-azure-soft sm:mt-0" />
          <span>
            管理者権限・テスト証明書は不要。<span className="inline-block">ダブルクリックで導入できます。</span>
          </span>
        </motion.p>
      </motion.div>

      <motion.div
        initial={{ opacity: 0, y: 60 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 1.4, delay: 0.5, ease }}
        className="relative mx-auto mt-16 max-w-5xl px-4 sm:mt-20 sm:px-6 [perspective:1600px]"
      >
        <motion.div style={{ y: tableY, rotateX: tableRotate, scale: tableScale }} className="origin-bottom">
          <TableMockup />
        </motion.div>
      </motion.div>

      <a
        href="#overview"
        className="mx-auto mt-16 flex w-fit flex-col items-center gap-2 text-[11px] uppercase tracking-[0.25em] text-ice-mute/70 transition hover:text-ice-mute"
      >
        Dive
        <ArrowDown className="h-4 w-4 animate-bounce" />
      </a>
    </section>
  );
}

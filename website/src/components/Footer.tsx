import { Download } from "lucide-react";
import { LogoMark } from "./Logo";
import { useDownload } from "./DownloadContext";
import { NAV_ITEMS, PRODUCT } from "../data/product";

export default function Footer() {
  const { startDownload } = useDownload();
  return (
    <footer className="relative border-t border-white/[0.08]">
      {/* 最終 CTA */}
      <div className="mx-auto max-w-4xl px-4 py-24 text-center sm:px-6">
        <LogoMark className="mx-auto h-14 w-14 drop-shadow-[0_0_24px_rgba(62,142,247,0.6)]" />
        <h2 className="text-gradient mt-8 text-balance text-4xl font-extrabold tracking-tightest sm:text-6xl">卓は、整いました。</h2>
        <button onClick={() => startDownload("installer")} className="btn-primary mt-10">
          <Download className="h-4 w-4" />
          {PRODUCT.version} を無料ダウンロード
        </button>
      </div>
      <div className="border-t border-white/[0.06]">
        <div className="mx-auto flex max-w-7xl flex-col items-center justify-between gap-5 px-4 py-8 text-xs text-ice-mute sm:flex-row sm:px-6 lg:px-8">
          <p>
            {PRODUCT.fullName} · {PRODUCT.version} · {PRODUCT.platform}
          </p>
          <ul className="flex flex-wrap justify-center gap-x-6 gap-y-2">
            {NAV_ITEMS.map((n) => (
              <li key={n.id}>
                <a href={`#${n.id}`} className="transition hover:text-ice">
                  {n.label}
                </a>
              </li>
            ))}
            <li>
              <a href="#faq" className="transition hover:text-ice">
                FAQ
              </a>
            </li>
          </ul>
        </div>
      </div>
    </footer>
  );
}

import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  // 相対パスでビルドし、ルート直下でも GitHub Pages のサブパス (/DTTS/) でも動くようにする
  base: "./",
  plugins: [react()],
});

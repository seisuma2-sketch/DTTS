/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      colors: {
        abyss: { 950: "#020814", 900: "#061329", 800: "#0B1F3F" },
        azure: { DEFAULT: "#3E8EF7", soft: "#60A5FA", cyan: "#22D3EE" },
        ice: { DEFAULT: "#F4F8FF", body: "#CBD5E1", mute: "#94A3B8" },
      },
      fontFamily: {
        sans: ["Inter", "Noto Sans JP", "system-ui", "sans-serif"],
      },
      letterSpacing: { tightest: "-0.045em" },
      keyframes: {
        rise: {
          "0%": { transform: "translateY(0)", opacity: "0" },
          "10%": { opacity: "var(--o, 0.5)" },
          "90%": { opacity: "var(--o, 0.5)" },
          "100%": { transform: "translateY(-110vh)", opacity: "0" },
        },
        caustic: {
          "0%,100%": { transform: "translate3d(0,0,0) scale(1)" },
          "50%": { transform: "translate3d(4%,-3%,0) scale(1.08)" },
        },
      },
      animation: {
        rise: "rise var(--d, 18s) linear infinite",
        caustic: "caustic 16s ease-in-out infinite",
      },
    },
  },
  plugins: [],
};

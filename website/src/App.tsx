import { MotionConfig } from "framer-motion";
import DeepSeaBackground from "./components/DeepSeaBackground";
import Navbar from "./components/Navbar";
import Hero from "./components/Hero";
import BentoFeatures from "./components/BentoFeatures";
import RulesShowcase from "./components/RulesShowcase";
import SecuritySection from "./components/SecuritySection";
import DownloadSection from "./components/DownloadSection";
import Faq from "./components/Faq";
import Footer from "./components/Footer";
import { DownloadProvider } from "./components/DownloadContext";

export default function App() {
  return (
    <MotionConfig reducedMotion="user">
      <DownloadProvider>
        <DeepSeaBackground />
        <Navbar />
        <main className="relative">
          <Hero />
          <BentoFeatures />
          <RulesShowcase />
          <SecuritySection />
          <DownloadSection />
          <Faq />
        </main>
        <Footer />
      </DownloadProvider>
    </MotionConfig>
  );
}

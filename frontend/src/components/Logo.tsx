// Логотип: значок (файл public/logo-icon.png) + название двумя цветами, как в оригинале
export default function Logo({ className = 'h-9' }: { className?: string }) {
  return (
    <span className="flex items-center gap-2.5">
      <img src="/logo-icon.png" alt="" className={`${className} w-auto`} />
      <span className="text-xl font-extrabold tracking-tight">
        <span className="text-navy">Skill</span>
        <span className="text-brand-600">Swap</span>
      </span>
    </span>
  )
}

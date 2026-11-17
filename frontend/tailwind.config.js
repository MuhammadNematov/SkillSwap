/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        // Тёмно-синий из логотипа
        navy: '#0b2257',
        // Фиолетовая палитра из макета
        brand: {
          50: '#f5f3ff',
          100: '#ede9fe',
          200: '#ddd6fe',
          300: '#c4b5fd',
          400: '#a78bfa',
          500: '#8b5cf6',
          600: '#6d4aff',
          700: '#5b3ad6',
          800: '#4a2fb0',
          900: '#2e1f6b',
        },
      },
      fontFamily: {
        sans: ['Inter', 'system-ui', 'Segoe UI', 'Roboto', 'sans-serif'],
      },
      borderRadius: {
        xl2: '1.25rem',
      },
      boxShadow: {
        card: '0 4px 24px -8px rgba(109, 74, 255, 0.18)',
        glass: '0 8px 32px 0 rgba(91, 58, 214, 0.12)',
      },
    },
  },
  plugins: [],
}

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
          50: '#f8f7ff',
          100: '#eee9ff',
          200: '#ddd8f0',
          300: '#b9a8f5',
          400: '#8b6cf6',
          500: '#7a55f0',
          600: '#6841e8',
          700: '#5a33da',
          800: '#4a2a9a',
          900: '#17143d',
        },
        ink: { DEFAULT: '#17143d', soft: '#777493', faint: '#a3a0bc', line: '#ece9f7' },
      },
      fontFamily: {
        sans: ['Inter', 'system-ui', 'Segoe UI', 'Roboto', 'sans-serif'],
      },
      borderRadius: {
        xl2: '1.5rem',
      },
      boxShadow: {
        card: '0 16px 32px -12px rgba(104, 65, 232, 0.22)',
        glass: '0 1px 2px rgba(23, 20, 61, 0.04)',
      },
    },
  },
  plugins: [],
}

import forms from "@tailwindcss/forms";

/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      colors: {
        brand: {
          50: "#f0fdfa",
          500: "#14b8a6",
          600: "#0d9488",
          700: "#0f766e"
        }
      }
    }
  },
  plugins: [forms]
};

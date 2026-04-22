# syntax=docker/dockerfile:1.7

FROM node:22-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY index.html ./
COPY tsconfig*.json ./
COPY vite.config.ts ./
COPY tailwind.config.js ./
COPY postcss.config.js ./
COPY eslint.config.js ./
COPY public ./public
COPY src ./src

ARG VITE_BRAIN_URL=/brain-api
ARG VITE_SELECTION_URL=/selection-api
ARG VITE_SUPPLIER_URL=/supplier-api
ARG VITE_STORE_URL=/store-api
ARG VITE_CONFIG_URL=/config-api

ENV VITE_BRAIN_URL=${VITE_BRAIN_URL} \
    VITE_SELECTION_URL=${VITE_SELECTION_URL} \
    VITE_SUPPLIER_URL=${VITE_SUPPLIER_URL} \
    VITE_STORE_URL=${VITE_STORE_URL} \
    VITE_CONFIG_URL=${VITE_CONFIG_URL}

RUN npm run build

FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 8080

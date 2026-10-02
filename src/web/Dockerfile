# Build context: src/web
# Output is static files, so the build stage runs natively on the build machine for every target arch.
FROM --platform=$BUILDPLATFORM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build

FROM nginx:alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/gmail-organiser/browser /usr/share/nginx/html
EXPOSE 80

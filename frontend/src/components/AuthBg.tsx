import { defineComponent } from "vue"
import { useTheme } from "~/composables/useTheme"

/**
 * 登录 / 安装页的满屏背景（移植旧前端 `pages/login/LoginBg.tsx` + `CornerTop/Bottom`）。
 *
 * 底色是浅蓝（暗色下深蓝），右上/左下各压一块 1337px 的渐变色块（#28aff0 → #120fc4）。
 * 两个 `<svg>` 都是纯装饰、不接收事件，所以 `pointer-events: none`，卡片照常可点。
 */
export default defineComponent({
  name: "AuthBg",
  setup() {
    const { isDark } = useTheme()
    return () => (
      <div class={["ol-auth-bg", isDark.value && "is-dark"]} aria-hidden="true">
        <svg class="ol-auth-blob ol-auth-blob-top" height="1337" width="1337">
          <defs>
            <path
              id="ol-blob-top-path"
              fill-rule="evenodd"
              d="M1337,668.5 C1337,1037.455193874239 1037.455193874239,1337 668.5,1337 C523.6725684305388,1337 337,1236 370.50000000000006,1094 C434.03835568300906,824.6732385973953 6.906089672974592e-14,892.6277623047779 0,668.5000000000001 C0,299.5448061257611 299.5448061257609,1.1368683772161603e-13 668.4999999999999,0 C1037.455193874239,0 1337,299.544806125761 1337,668.5Z"
            />
            <linearGradient id="ol-blob-top-grad" x1="0.79" y1="0.62" x2="0.21" y2="0.86">
              <stop offset="0" stop-color="#28aff0" stop-opacity="1" />
              <stop offset="1" stop-color="#120fc4" stop-opacity="1" />
            </linearGradient>
          </defs>
          <use href="#ol-blob-top-path" fill="url(#ol-blob-top-grad)" />
        </svg>

        <svg class="ol-auth-blob ol-auth-blob-bottom" height="896" width="967.89">
          <defs>
            <path
              id="ol-blob-bottom-path"
              fill-rule="evenodd"
              d="M896,448 C1142.6325445712241,465.5747656464056 695.2579309733121,896 448,896 C200.74206902668806,896 5.684341886080802e-14,695.2579309733121 0,448.0000000000001 C0,200.74206902668806 200.74206902668791,5.684341886080802e-14 447.99999999999994,0 C695.2579309733121,0 475,418 896,448Z"
            />
            <linearGradient id="ol-blob-bottom-grad" x1="0.5" y1="0" x2="0.5" y2="1">
              <stop offset="0" stop-color="#28aff0" stop-opacity="1" />
              <stop offset="1" stop-color="#120fc4" stop-opacity="1" />
            </linearGradient>
          </defs>
          <use href="#ol-blob-bottom-path" fill="url(#ol-blob-bottom-grad)" />
        </svg>
      </div>
    )
  },
})

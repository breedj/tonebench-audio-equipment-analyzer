import { createApp } from 'vue'
import './style.css'
// Side-effect import: sets the <html>.dark class from the stored/OS theme before first paint,
// same reasoning as importing style.css itself before App.
import './composables/useAppTheme'
import App from './App.vue'

createApp(App).mount('#app')

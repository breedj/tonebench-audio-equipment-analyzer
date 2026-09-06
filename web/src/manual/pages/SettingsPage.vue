<template>
  <div>
    <h1>Configuration and status bar</h1>

    <h2>Configuration dialog</h2>
    <p>
      The gear button at the top right. Everything in it is global — it applies whichever
      measurement is selected, and is not part of any measurement's saved settings.
    </p>
    <table>
      <thead>
        <tr>
          <th>Setting</th>
          <th>What it does</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Style</td>
          <td>
            Light, Dark, or Auto to follow the operating system. The charts repaint with it, since
            they are drawn on a canvas rather than styled by the page.
          </td>
        </tr>
        <tr>
          <td>Capture delay (ms)</td>
          <td>
            How long to wait after the generator is enabled before capture actually starts. It lets
            the output settle — through external gear, or through a DUT with a slow input stage —
            before the FFT begins averaging. Raise it if the first result of a run is consistently
            different from the ones after it.
          </td>
        </tr>
        <tr>
          <td>Metrics bandwidth (Hz)</td>
          <td>
            The band THD+N, SNR and the noise floor are integrated over, and what keeps DC out of
            that integration. 20 Hz–20 kHz gives audio-band figures; widening it includes noise the
            ear cannot hear and makes every noise-based number look worse.
          </td>
        </tr>
        <tr>
          <td>Step-down delay (ms)</td>
          <td>
            How long a peak must stay under-range before the input range steps down to a more
            sensitive setting. Higher values ride out an amplitude-modulated signal — two close
            tones beating, for instance — without chasing every dip; lower values react faster to a
            genuinely quieter signal.
          </td>
        </tr>
        <tr>
          <td>Check for updates</td>
          <td>
            Periodically checks for a newer release and shows a dismissible banner. It never
            installs anything, and fails silently on a machine with no internet access.
          </td>
        </tr>
      </tbody>
    </table>

    <h2>Status bar</h2>
    <p>Along the bottom of the window, left to right.</p>
    <table>
      <thead>
        <tr>
          <th>Item</th>
          <th>Means</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Green dot + name</td>
          <td>A QA403 is open and ready. Serial number and firmware version are shown beside it.</td>
        </tr>
        <tr>
          <td>Disconnected</td>
          <td>
            No device found. The app starts fine without one, so this is a normal state; press
            <strong>Connect</strong> after plugging one in.
          </td>
        </tr>
        <tr>
          <td>Device removed</td>
          <td>
            The QA403 stopped responding — unplugged or powered off mid-session. Plug it back in and
            press Connect; no restart is needed.
          </td>
        </tr>
        <tr>
          <td>Simulated</td>
          <td>
            No hardware in the loop: signals are synthesized with a known amplitude, distortion and
            noise floor. The two checkboxes beside it add a simulated noise floor and simulated
            50 Hz mains hum to the synthetic DUT.
          </td>
        </tr>
        <tr>
          <td>Acquiring</td>
          <td>A measurement is running.</td>
        </tr>
        <tr>
          <td>n dropped / n blocks dropped</td>
          <td>
            The host fell behind the USB stream. Each dropped chunk or block is a hole in the sample
            stream, which splices the record being analysed — it smears tones into skirts and lifts
            the broadband floor. These appear only when it happens, because it should not: treat any
            non-zero count as "the numbers on screen are wrong" and re-run the measurement.
          </td>
        </tr>
        <tr>
          <td>Attenuation</td>
          <td>
            The input range in use. Read-only — it is kept matched to the signal automatically.
          </td>
        </tr>
        <tr>
          <td>Output range</td>
          <td>
            The output attenuator in use. Read-only, and derived from the generator's amplitude: it
            is an analog attenuator whose setting sets the DAC's noise floor, so the lowest range
            with enough headroom is always chosen. This is also why the measured noise floor can
            step when you change the generator level across one of its boundaries — that is the
            attenuator changing, not the DSP.
          </td>
        </tr>
      </tbody>
    </table>

    <h2>Keyboard and window</h2>
    <ul>
      <li><strong>Escape</strong> closes any dialog, including this manual.</li>
      <li>
        The app is an ordinary browser tab: it can be reloaded, bookmarked and zoomed with the
        browser's own controls, and closing it does not stop the server or the device.
      </li>
      <li>
        Closing the console window the app was started from does shut everything down cleanly — wait
        for it rather than killing the process, which is what leaves a QA403 needing a power cycle.
      </li>
    </ul>
  </div>
</template>

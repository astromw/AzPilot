# AZ-EQ6 supervised hardware checklist

Do not skip steps or test with an unattended mount. Remove the telescope or use a light dummy load for the first direction test if practical. Keep clear of the counterweight bar, tripod legs, cables and OTA.

## Before connecting

- [ ] Mount is physically assembled in AZ configuration and the matching model profile is selected.
- [ ] A compatible direct motor-controller USB/serial connection is available; the model selector does not enable hand-controller PC Direct mode.
- [ ] Tripod is stable and level; clutches and cables are secure.
- [ ] OTA and accessories have full clearance in the intended test direction.
- [ ] Physical power switch is within immediate reach.
- [ ] SynScan and every other mount-control application are closed.
- [ ] USB-B cable is connected directly to the mount.
- [ ] Correct COM port is selected; baud rate is 115200.
- [ ] Manual rate is 0.05–0.25 °/s; GoTo profile is slow (0.5 or below). Do not start commissioning at 3–4°/s.
- [ ] Minimum altitude is appropriate for your tripod and surroundings.

## Commissioning sequence

1. **Read-only sanity check:** connect and inspect firmware, position, and status. Disconnect if values jump without movement.
2. **Azimuth:** hold RIGHT briefly, release, and confirm direction and stopping. Repeat LEFT.
3. **Altitude:** hold UP briefly, release, and confirm direction and stopping. Repeat DOWN with clearance.
4. **Emergency stop:** repeat a short slew and test the red STOP BOTH AXES button, Space, and Escape. Separately verify that centre-pad STOP cancels a GoTo and resumes the previous tracking rate.
5. **Small GoTo:** choose a target only 1–2° away in a clear direction. Abort it once, then allow another small GoTo to finish.
6. **Reference:** only if the tube is physically north and level, use Set home reference. Otherwise use a known target and Sync. For multi-point alignment, first verify single-point GoTo, then add at least three widely separated known targets and inspect the model RMS before longer slews.
7. **Tracking:** start sidereal tracking for one minute and confirm smooth motion and stable RA/Dec. Make a short manual movement and a small GoTo; confirm tracking resumes only after normal completion. Confirm STOP BOTH AXES and an altitude-limit stop do not resume motion. Stop tracking.
8. **Park:** while stopped in a safe position, choose Set current. Move only 1–2° away, Park, verify the return and parked lock, then Unpark. Repeat once and abort the park slew.
9. **Pulse guide:** start tracking, use a 100–500 ms pulse at the default guide rate, and verify the small correction and tracking resumption. Test Up/Down/Left/Right individually and confirm that only the requested axis receives the correction. Proceed only with adequate clearance. Confirm AbortSlew cancels a longer test pulse.
10. **ASCOM:** connect a client, read coordinates, issue a small MoveAxis, then AbortSlew. Test a small asynchronous GoTo, short pulse and park/unpark if your client uses them. Confirm that client disconnect leaves the mount connection under AzPilot's desktop control; remember that client disconnect does not stop motion.
11. **Recovery:** first test disconnect/reconnect while motion is stopped. Desktop Disconnect intentionally preserves the last motion state, so verify this later only under close supervision. Never intentionally interrupt USB during a slew for an early test.

Record any firmware-specific differences or unexpected direction behavior before increasing speed. Do not use high-speed GoTo until slow GoTo and abort are proven reliable on your mount.

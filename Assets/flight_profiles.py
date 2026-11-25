"""Launch flight profiles, vanilla vs VLS Fix, from the vanilla ammo definitions (Ammos.sbc) and the mod's VLS.xml.
Simulated frame by frame at 60 fps the way VlsTorpedoBoost does it. Usage: python3 flight_profiles.py [out.png]  (default: flight_profiles.png next to this script)
Needs numpy and matplotlib (pip install numpy matplotlib) and a Space Engineers install.
SE_DIR overrides the Space Engineers install path (fonts come from its Tools/Fonts)."""
import sys
import os
import math
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib import font_manager

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "flight_profiles.png")
DT = 1 / 60

# ---------- vanilla definitions (Content/Data/Ammos.sbc) ----------
HOMING = dict(name="MissileHoming", v0=100.0, acc=50.0, vmax=150.0, rate=16.0)
TORPEDO = dict(name="Torpedo", v0=35.0, acc=35.0, vmax=150.0, rate=14.0)

# ---------- mod config (Content/Data/VLS.xml) ----------
HOMING_BOOST, HOMING_MULT, HOMING_SPEEDF = 2.5, 5.0, 0.5
EXIT, RAMP = 0.3, 0.7
REF_RATE, AGILITY_EXP, MAX_ANG_ACC = 14.0, 1.5, 80.0
BRAKE_MARGIN, SPEED_GAIN, AIM_GAIN = 1.0, 20.0, 4.0
SETTLE_ANGLE, SETTLE_RATE = 0.5, 2.0
TURN_ANGLE = 90.0                       # example: target 90° off the launch axis

T = 5.0
N = int(T / DT) + 1
t = np.arange(N) * DT


def vanilla(d):
    v = np.minimum(d["vmax"], d["v0"] + d["acc"] * t)
    return np.full(N, d["rate"]), v


def vls_homing(d):
    rate, speed, cap_line = np.empty(N), np.empty(N), np.empty(N)
    v = d["v0"]
    for i, x in enumerate(t):
        k = min(max(x / HOMING_BOOST, 0.0), 1.0)
        cap = d["vmax"] * (HOMING_SPEEDF + (1 - HOMING_SPEEDF) * k)
        v = min(v, cap)                                  # FlyHoming clamps to the cap
        rate[i] = d["rate"] * (HOMING_MULT + (1 - HOMING_MULT) * k)
        speed[i] = v
        cap_line[i] = cap
        v = min(d["vmax"], v + d["acc"] * DT)            # game acceleration next frame
    return rate, speed, cap_line


def vls_torpedo(d):
    agility = d["rate"] / REF_RATE
    rot = agility ** AGILITY_EXP
    a = math.radians(MAX_ANG_ACC) * rot
    aim_gain = AIM_GAIN * math.sqrt(rot)
    rate, speed = np.empty(N), np.empty(N)
    angle, w = math.radians(TURN_ANGLE), 0.0
    turn_end = None
    for i, x in enumerate(t):
        if turn_end is None:
            speed[i] = d["v0"]                           # held at launch speed through exit and turn
            if x >= EXIT:                                # FlyTurn, reduced to the turn plane
                curve = min(math.sqrt(2 * BRAKE_MARGIN * a * abs(angle)), aim_gain * abs(angle))
                curve = math.copysign(curve, angle)
                acc = max(-a, min(a, (curve - w) * SPEED_GAIN))
                w += acc * DT
                angle -= w * DT
                if math.degrees(abs(angle)) < SETTLE_ANGLE and math.degrees(abs(w)) < SETTLE_RATE:
                    turn_end = x
            rate[i] = abs(math.degrees(w))
        else:
            r = min((x - turn_end) / RAMP, 1.0)          # ramp launch speed -> DesiredSpeed
            speed[i] = d["v0"] + (d["vmax"] - d["v0"]) * r
            rate[i] = d["rate"]                          # seeker in charge, vanilla turn rate
    return rate, speed, turn_end


hv_rate, hv_speed = vanilla(HOMING)
h_rate, h_speed, h_cap = vls_homing(HOMING)
tv_rate, tv_speed = vanilla(TORPEDO)
t_rate, t_speed, t_turn_end = vls_torpedo(TORPEDO)
t_peak_i = int(np.argmax(t_rate))
print(f"torpedo turn ends at {t_turn_end:.2f}s, peak {t_rate[t_peak_i]:.1f} deg/s at {t[t_peak_i]:.2f}s")

# ---------- style ----------
SURF = "#1b2838"
INK = "#ffffff"
INK2 = "#e6e9ed"
MUTED = "#6b7682"
GRID = "#2a333e"
TURN_C = "#3987e5"
SPEED_C = "#fb0100"

SE_DIR = os.environ.get("SE_DIR", os.path.expanduser("~/.local/share/Steam/steamapps/common/SpaceEngineers"))
FONT_DIR = os.path.join(SE_DIR, "Tools", "Fonts")
for f in ("space_engineers_regular.ttf", "space_engineers_bold.ttf"):
    font_manager.fontManager.addfont(os.path.join(FONT_DIR, f))
BOLD = "space_engineers_bold"

plt.rcParams.update({
    "font.family": "space_engineers_regular", "font.size": 11, "text.color": INK,
    "axes.labelcolor": INK2, "xtick.color": MUTED, "ytick.color": MUTED,
})

RATE_TOP, SPEED_TOP = 100, 150          # gridlines shared: 20 °/s == 30 m/s
PAD = 1.06

fig, axes = plt.subplots(2, 2, figsize=(12.72, 9.6), dpi=100)
fig.patch.set_facecolor(SURF)
plt.subplots_adjust(left=0.07, right=0.93, top=0.815, bottom=0.085, wspace=0.3, hspace=0.42)


def profile(ax, title, rate, speed, phases=(), cap_line=None):
    ax.set_facecolor(SURF)
    sp = ax.twinx()
    for a_ in (ax, sp):
        for s in ("top", "right", "left"):
            a_.spines[s].set_visible(False)
        a_.spines["bottom"].set_color(MUTED)
        a_.tick_params(length=0, labelsize=10.5)
        a_.set_xlim(0, T)
    ax.set_ylim(0, RATE_TOP * PAD)
    sp.set_ylim(0, SPEED_TOP * PAD)
    ax.set_yticks(np.arange(0, RATE_TOP + 1, 20))
    sp.set_yticks(np.arange(0, SPEED_TOP + 1, 30))
    ax.grid(axis="y", color=GRID, linewidth=1)
    ax.set_axisbelow(True)
    ax.set_title(title, loc="left", fontsize=15, family=BOLD, fontweight="bold", color=INK, pad=10)
    ax.set_ylabel("Turn rate (°/s)", fontsize=11, color=TURN_C)
    sp.set_ylabel("Speed (m/s)", fontsize=11, color=SPEED_C, rotation=270, labelpad=16)
    ax.set_xlabel("Time since launch (s)", fontsize=11)
    for x0, x1, label in phases:
        ax.axvspan(x0, x1, color="#ffffff", alpha=0.045, lw=0)
        if label:
            ax.text(x0 + 0.06, RATE_TOP * PAD * 0.985, label, ha="left", va="top", fontsize=9.5, color=INK2)
    if cap_line is not None:
        sp.plot(t, cap_line, color=MUTED, lw=1.2, ls=(0, (4, 3)))
    sp.plot(t, speed, color=SPEED_C, lw=2.6, solid_capstyle="round")
    sp.plot(t, rate * SPEED_TOP / RATE_TOP, color=TURN_C, lw=2.6, solid_capstyle="round")
    return ax, sp


# Profile 1: homing missile
profile(axes[0, 0], "Homing missile: vanilla", hv_rate, hv_speed)
ax, sp = profile(axes[0, 1], "Homing missile: VLS Fix", h_rate, h_speed,
                 phases=[(0, HOMING_BOOST, f"boost: {HOMING_MULT:.0f}× turn rate\nspeed capped")])
ax.text(0.06, h_rate[0] + 2, f"{h_rate[0]:.0f}°/s", fontsize=10, color=INK2, va="bottom")

# Profile 2: torpedo
profile(axes[1, 0], "Torpedo: vanilla", tv_rate, tv_speed)
ax, sp = profile(axes[1, 1], "Torpedo: VLS Fix", t_rate, t_speed,
                 phases=[(0, EXIT, ""), (EXIT, t_turn_end, f"Jet turn (target at {TURN_ANGLE:.0f}°)\nDepends on the target position")])
pk_t, pk_r = t[t_peak_i], t_rate[t_peak_i]
ax.annotate(f"peak {pk_r:.0f}°/s", xy=(pk_t, pk_r), xytext=(pk_t + 0.75, pk_r - 4),
            fontsize=10, color=INK2, arrowprops=dict(arrowstyle="-", color=MUTED, lw=1))

# vanilla turn-rate labels
for a_, d in ((axes[0, 0], HOMING), (axes[1, 0], TORPEDO)):
    a_.text(T - 0.06, d["rate"] + 2, f"{d['rate']:.0f}°/s", fontsize=10, color=INK2, ha="right", va="bottom")

# Header + legend
fig.text(0.07, 0.955, "Launch flight profile: vanilla vs. VLS Fix", fontsize=21, family=BOLD, fontweight="bold", color=INK)
fig.text(0.07, 0.918, "Slower while turning = tighter turn radius. Full speed once pointed at the target.",
         fontsize=12.5, color=INK2)
lx = 0.07
for name, col in (("Turn rate (left axis)", TURN_C), ("Speed (right axis)", SPEED_C)):
    fig.add_artist(plt.Line2D([lx, lx + 0.03], [0.878, 0.878], color=col, lw=3.5,
                              solid_capstyle="round", transform=fig.transFigure))
    fig.text(lx + 0.038, 0.878, name, va="center", fontsize=12.5, color=INK)
    lx += 0.25
fig.text(0.93, 0.025, "Vanilla MissileHoming and Torpedo definitions, default mod config, launched from a stationary grid. "
         f"Simulated launch with Python {sys.version.split()[0]}, Graphs from matplotlib {matplotlib.__version__}.",
         ha="right", fontsize=9.5, color=MUTED)

fig.savefig(OUT, facecolor=SURF)
print("saved", OUT)

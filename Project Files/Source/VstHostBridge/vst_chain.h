/*  vst_chain.h

This file is part of a program that implements a Software-Defined Radio.

This code/file can be found on GitHub : https://github.com/nubbyless/Thetis-Plus

Copyright (C) 2026 ChasingCoffee
Copyright (C) 2026 nubbyless <nubbyless@yahoo.com>
Portions Copyright (C) 2026 Daniel Poznanski, 3Z9AM - changes in this fork (see its git history).

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
*/

#ifndef _vst_chain_h
#define _vst_chain_h

#include "vst_host_bridge.h"

struct ThetisPluginRuntime;
struct VstEditorSession;
typedef void (*VstChainStateDirtyCallback)(void* context);

enum
{
	VST_MAX_CHAIN_PLUGINS = 16,
	VST_LANE_MAX_DELAY_FRAMES = 32768	/* latency compensation between the parallel paths */
};

struct VstPluginSlot
{
	volatile LONG enabled;
	volatile LONG bypass;
	volatile LONG load_state;
	volatile LONG format;
	volatile LONG lane;
	wchar_t path[VST_MAX_PLUGIN_PATH_CHARS];
	wchar_t name[VST_MAX_PLUGIN_NAME_CHARS];
	wchar_t cid[VST_MAX_PLUGIN_CID_CHARS];
};

struct VstProcessingPlugin
{
	volatile LONG enabled;
	volatile LONG bypass;
	volatile LONG load_state;
	volatile LONG lane;
	int latency;	/* last latency the plugin reported, samples */
	ThetisPluginRuntime* runtime;
};

struct VstProcessingState
{
	volatile LONG ref_count;
	int sample_rate;
	int max_block_size;
	int num_channels;
	int plugin_count;
	VstProcessingPlugin plugins[VST_MAX_CHAIN_PLUGINS];
	double* lane_buffer;			/* path B block, max_block_size * num_channels */
	double* lane_delay[VST_CHAIN_LANES];	/* VST_LANE_MAX_DELAY_FRAMES * num_channels each */
	int lane_delay_pos;
};

struct VstChainState
{
	SRWLOCK state_lock;
	CRITICAL_SECTION mutation_lock;
	HANDLE rebuild_event;
	HANDLE worker_thread;
	volatile LONG stop_worker;
	volatile LONG rebuild_generation;
	volatile LONG created;
	volatile LONG ready;
	volatile LONG bypass;
	volatile LONG plugin_count;
	volatile LONG64 gain_bits;
	volatile LONG64 lane_gain_bits[VST_CHAIN_LANES];
	int sample_rate;
	int max_block_size;
	int num_channels;
	VstChainStateDirtyCallback state_dirty_callback;
	void* state_dirty_context;
	VstPluginSlot plugins[VST_MAX_CHAIN_PLUGINS];
	VstProcessingState* active_state;
};

void VstProcessingState_Retain(VstProcessingState* state);
void VstProcessingState_Release(VstProcessingState*& state);

void VstChain_Initialize(VstChainState& chain);
void VstChain_Terminate(VstChainState& chain);
void VstChain_Reset(VstChainState& chain);
int VstChain_Create(VstChainState& chain, int sample_rate, int max_block_size, int num_channels);
void VstChain_Destroy(VstChainState& chain);

void VstChain_SetBypass(VstChainState& chain, int bypass);
int VstChain_GetBypass(const VstChainState& chain);
int VstChain_GetReady(const VstChainState& chain);

void VstChain_SetGain(VstChainState& chain, double gain);
double VstChain_GetGain(const VstChainState& chain);

int VstChain_ClearPlugins(VstChainState& chain);
int VstChain_AddPlugin(VstChainState& chain, const wchar_t* plugin_path, const wchar_t* plugin_cid);
int VstChain_RemovePlugin(VstChainState& chain, int index);
int VstChain_MovePlugin(VstChainState& chain, int from_index, int to_index);
void VstChain_GetSnapshotInfo(
	const VstChainState& chain,
	int& created,
	int& sample_rate,
	int& max_block_size,
	int& num_channels,
	int& ready,
	int& bypass,
	double& gain,
	int& plugin_count);
int VstChain_GetPluginCount(const VstChainState& chain);
int VstChain_GetPluginInfo(const VstChainState& chain, int index, VstPluginInfo* info);
int VstChain_SetPluginBypass(VstChainState& chain, int index, int bypass);
int VstChain_SetPluginEnabled(VstChainState& chain, int index, int enabled);
int VstChain_SetPluginLane(VstChainState& chain, int index, int lane);
void VstChain_SetLaneGain(VstChainState& chain, int lane, double gain);
double VstChain_GetLaneGain(const VstChainState& chain, int lane);
int VstChain_GetPluginStateSize(VstChainState& chain, int index);
int VstChain_GetPluginState(VstChainState& chain, int index, void* buffer, int buffer_size, int* bytes_written);
int VstChain_SetPluginState(VstChainState& chain, int index, const void* buffer, int buffer_size);
void VstChain_SetStateDirtyCallback(VstChainState& chain, VstChainStateDirtyCallback callback, void* context);
int VstChain_OpenPluginEditor(VstChainState& chain, int index, HWND parent_window, int& width, int& height, int& can_resize, VstEditorSession*& session);
int VstChain_ClosePluginEditor(VstEditorSession*& session);
int VstChain_ResizePluginEditor(VstEditorSession* session, int width, int height);

int VstChain_ProcessInterleavedDouble(VstChainState& chain, double* buffer, int frames);

#endif

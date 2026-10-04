import { useState, useEffect, useCallback } from 'react';
import { APIProvider } from '@vis.gl/react-google-maps';
import { motion } from 'framer-motion';
import type { Place, Message, UIState } from './types';
import MapBoard from './components/MapBoard';
import ChatBoard from './components/ChatBoard';
import SliderBoard from './components/SliderBoard';
import StartSearch from './components/StartSearch';

const ROUTE_KEY = 'guaid.route';
const PLAN_QUEUE_KEY = 'guaid.planQueue';

interface PlanStep {
  label: string;
  queries: { text: string; named: boolean }[];
}
type SearchMode = 'spot' | 'plan';

export default function App() {
  const [route, setRoute] = useState<Place[]>(() => 
    JSON.parse(localStorage.getItem(ROUTE_KEY) ?? '[]')
  );
  const [planQueue, setPlanQueue] = useState<PlanStep[]>(() => 
    JSON.parse(localStorage.getItem(PLAN_QUEUE_KEY) ?? '[]')
  );
  const [searchMode, setSearchMode] = useState<SearchMode>('spot');
  const [mapsKey, setMapsKey] = useState<string | null>(null);
  
  const [messages, setMessages] = useState<Message[]>([]);
  const [isPanelExpanded, setIsPanelExpanded] = useState(true);
  const [activePreviewId, setActivePreviewId] = useState<string | null>(null);
  const [isRouteManagerOpen, setIsRouteManagerOpen] = useState(false);
  const [selectedRoutePlace, setSelectedRoutePlace] = useState<Place | null>(null);

  useEffect(() => {
    const routeToSave = route.map(({ photos, ...rest }) => rest);
    localStorage.setItem(ROUTE_KEY, JSON.stringify(routeToSave));
  }, [route]);

  useEffect(() => {
    localStorage.setItem(PLAN_QUEUE_KEY, JSON.stringify(planQueue));
  }, [planQueue]);

  useEffect(() => {
    let cancelled = false;
    fetch('/api/config')
      .then(response => {
        if (!response.ok) throw new Error('Config request failed');
        return response.json();
      })
      .then(data => {
        if (!cancelled) setMapsKey(data.googleMapsApiKey ?? '');
      })
      .catch(() => {
        if (!cancelled) setMapsKey('');
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (route.length === 0) {
      setMessages([{ id: 'msg_1', sender: 'ai', text: "Hi! Where are you starting your trip from today?" }]);
      setIsPanelExpanded(true);
    }
  }, [route.length]);

  const activePendingMessage = messages.find(m => m.pendingLocations && m.pendingLocations.length > 0);
  const uiState: UIState = activePendingMessage ? 'slider' : 'chat';
  const isTyping = messages.some(m => m.isTyping);

  const askRoute = async (prompt: string, path: Place[], currentMode: SearchMode, step?: PlanStep) => {
    const from = path[path.length - 1];
    
    const payload: any = {
      prompt,
      mode: currentMode,
      start: {
        name: from.name,
        google_place_id: from.google_place_id,
        lat: from.lat,
        lng: from.lng
      },
      visited: path.map(place => place.google_place_id)
    };

    if (step) {
      payload.step = step;
    }

    const response = await fetch('/api/route', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });

    if (!response.ok) {
      const problem = await response.json().catch(() => null);
      throw new Error(problem?.detail ?? 'Request failed');
    }

    const data = await response.json();
    
    const processedLocations = (data.locations || []).map((loc: any, index: number) => ({
      ...loc,
      google_place_id: loc.google_place_id || `temp_${Date.now()}_${index}`,
    }));

    return { text: data.text, locations: processedLocations, plan: data.plan || [] };
  };

const handleStartSelect = useCallback((place: Place) => {
  setRoute([place]);
  setMessages(prev => [
    ...prev,
    { id: `user_${Date.now()}`, sender: 'user', text: place.name },
    { id: `ai_${Date.now()}`, sender: 'ai', text: "Great start! What are we going to do? Any specific goals?" }
  ]);
  setIsPanelExpanded(true);
}, []);

  const handleSendMessage = async (text: string) => {
    if (route.length === 0) return;

    if (searchMode === 'spot') {
      setPlanQueue([]);
    }

    const typingId = `typing_${Date.now()}`;
    setMessages(prev => [
      ...prev, 
      ...(text ? [{ id: `user_${Date.now()}`, sender: 'user' as const, text }] : []),
      { id: typingId, sender: 'ai' as const, isTyping: true, text: '' }
    ]);
    setIsPanelExpanded(true);
    setActivePreviewId(null);

    try {
      const { text: aiText, locations, plan } = await askRoute(text, route, searchMode);
      
      if (searchMode === 'plan') {
         setPlanQueue(plan);
      }

      setMessages(prev => {
        const filtered = prev.filter(m => m.id !== typingId);
        return [...filtered, { 
          id: `ai_${Date.now()}`, 
          sender: 'ai', 
          text: aiText,
          pendingLocations: locations.length > 0 ? locations : undefined
        }];
      });
    } catch (error) {
      setMessages(prev => {
        const filtered = prev.filter(m => m.id !== typingId);
        return [...filtered, { id: `ai_${Date.now()}`, sender: 'ai', text: "Please try again." }];
      });
    }
  };

  const processNextPlanStep = async (currentRoute: Place[], remainingQueue: PlanStep[]) => {
    if (remainingQueue.length === 0) return;
    
    const nextStep = remainingQueue[0];
    const newQueue = remainingQueue.slice(1);
    setPlanQueue(newQueue);
    setIsPanelExpanded(true);

    const typingId = `typing_auto_${Date.now()}`;
    setMessages(prev => [
      ...prev,
      { id: typingId, sender: 'ai' as const, isTyping: true, text: '' }
    ]);

    try {
      const { text: aiText, locations } = await askRoute(nextStep.label, currentRoute, 'plan', nextStep);
      setMessages(prev => {
         const filtered = prev.filter(m => m.id !== typingId);
         return [...filtered, { 
           id: `ai_${Date.now()}`, 
           sender: 'ai', 
           text: aiText,
           pendingLocations: locations.length > 0 ? locations : undefined
         }];
      });
    } catch (error) {
       setMessages(prev => {
         const filtered = prev.filter(m => m.id !== typingId);
         return [...filtered, { id: `ai_${Date.now()}`, sender: 'ai', text: "Error finding next places. Try again." }];
       });
    }
  };

  const handleSwipeUp = (messageId: string, googlePlaceId: string) => {
    let swipedLocation: Place | undefined;

    setMessages(prev => prev.map(msg => {
      if (msg.id === messageId && msg.pendingLocations) {
        swipedLocation = msg.pendingLocations.find(l => l.google_place_id === googlePlaceId);
        return { ...msg, pendingLocations: undefined };
      }
      return msg;
    }));

    if (swipedLocation) {
      const nextRoute = [...route, swipedLocation];
      setRoute(nextRoute);
      setActivePreviewId(null);
      setIsPanelExpanded(false);
      
      setMessages(prev => [...prev, { 
        id: `ai_${Date.now()}`, 
        sender: 'ai', 
        text: `${swipedLocation!.name} added to your route!` 
      }]);

      if (planQueue.length > 0) {
        processNextPlanStep(nextRoute, planQueue);
      }
    }
  };

  const handleSkipStep = () => {
    if (planQueue.length > 0) {
      setMessages(prev => [...prev, { 
        id: `user_skip_${Date.now()}`, 
        sender: 'user', 
        text: `Let's find: ${planQueue[0].label}` 
      }]);
      processNextPlanStep(route, planQueue);
    }
  };

  const handleCloseSlider = () => {
    setActivePreviewId(null);
    setMessages(prev => prev.map(msg => {
      if (msg.pendingLocations) {
        return {
          ...msg,
          pendingLocations: undefined,
          text: msg.text || "Alright, let's explore other options! What else would you like to see?"
        };
      }
      return msg;
    }));
    setIsPanelExpanded(true);
  };

  const handleReset = () => {
    setRoute([]);
    setPlanQueue([]);
    setSearchMode('spot');
    setActivePreviewId(null);
    setMessages([{ id: `msg_${Date.now()}`, sender: 'ai', text: "Hi! Where are you starting your trip from today?" }]);
    setIsPanelExpanded(true);
    setIsRouteManagerOpen(false);
    setSelectedRoutePlace(null);
  };

  const getGoogleMapsUrl = () => {
    if (route.length === 0) return '#';
    if (route.length === 1) {
      return `https://www.google.com/maps/search/?api=1&query=${route[0].lat},${route[0].lng}&query_place_id=${route[0].google_place_id}`;
    }
    const origin = `${route[0].lat},${route[0].lng}`;
    const destination = `${route[route.length - 1].lat},${route[route.length - 1].lng}`;
    const waypoints = route.slice(1, -1).map(loc => `${loc.lat},${loc.lng}`).join('|');
    const waypointIds = route.slice(1, -1).map(loc => loc.google_place_id).join('|');
    
    let url = `https://www.google.com/maps/dir/?api=1&origin=${origin}&origin_place_id=${route[0].google_place_id}&destination=${destination}&destination_place_id=${route[route.length - 1].google_place_id}&travelmode=walking`;
    if (waypoints) {
      url += `&waypoints=${waypoints}&waypoint_place_ids=${waypointIds}`;
    }
    return url;
  };

  const moveRouteItem = (index: number, direction: -1 | 1) => {
    const newRoute = [...route];
    const targetIndex = index + direction;
    if (targetIndex < 0 || targetIndex >= newRoute.length) return;
    
    const temp = newRoute[index];
    newRoute[index] = newRoute[targetIndex];
    newRoute[targetIndex] = temp;
    
    setRoute(newRoute);
  };

  const removeRouteItem = (index: number) => {
    const newRoute = route.filter((_, i) => i !== index);
    if (newRoute.length === 0) {
      handleReset();
    } else {
      setRoute(newRoute);
    }
  };

  const handlePreviewClick = (placeId: string) => {
    setActivePreviewId(placeId);
    setIsPanelExpanded(true);
  };

  const handleRouteClick = (placeId: string) => {
    const place = route.find(p => p.google_place_id === placeId);
    if (place) setSelectedRoutePlace(place);
  };

  if (mapsKey === null) {
    return (
      <div className="w-full h-[100dvh] bg-gray-100 flex items-center justify-center">
        <img src="/logo.png" alt="guAId" className="h-16 w-16 object-contain" />
      </div>
    );
  }

  return (
    <APIProvider apiKey={mapsKey}>
      <div className="relative w-full h-[100dvh] overflow-hidden bg-gray-100 font-['Inter',sans-serif]">
        <div className="absolute top-6 left-6 z-20 pointer-events-none flex items-center justify-between w-[calc(100%-3rem)]">
        <div className="bg-white p-2.5 rounded-2xl shadow-md pointer-events-auto flex items-center justify-center">
            <img 
              src="/logo.png" 
              alt="guAId logo" 
              className="h-10 w-10 object-contain" 
            />
          </div>
          {route.length > 0 && (
            <button 
              onClick={handleReset}
              className="pointer-events-auto bg-white/80 backdrop-blur text-red-600 px-4 py-2 rounded-xl text-sm font-bold shadow-sm hover:bg-white active:scale-95 transition-all"
            >
              Reset
            </button>
          )}
        </div>

      <div className="absolute top-0 left-0 w-full h-[100dvh] transition-all duration-500 z-0">
        <MapBoard 
          confirmedRoute={route}
          pendingLocations={activePendingMessage?.pendingLocations || []}
          activePreviewId={activePreviewId}
          isHalfScreen={isPanelExpanded && uiState === 'slider'}
          onPreviewClick={handlePreviewClick}
          onRouteClick={handleRouteClick}
        />
      </div>

<div className={`absolute bottom-0 w-full md:max-w-md md:left-1/2 md:-translate-x-1/2 bg-white/95 backdrop-blur-xl transition-all duration-500 ease-in-out shadow-[0_-10px_50px_rgba(0,0,0,0.1)] flex flex-col z-10
  ${isPanelExpanded ? (uiState === 'slider' ? 'h-[50dvh]' : 'max-h-[85dvh] h-[85dvh]') : 'max-h-[15dvh] h-[15dvh]'} rounded-t-[2rem]
`}>
          
          {uiState === 'slider' && isPanelExpanded && (
            <button 
              onClick={handleCloseSlider}
              className="absolute -top-16 right-6 bg-black/80 backdrop-blur-md text-white p-3 rounded-full shadow-lg z-50 hover:bg-black transition-colors"
            >
              <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.5" d="M6 18L18 6M6 6l12 12"></path>
              </svg>
            </button>
          )}

          {isPanelExpanded && (
            <div 
              className="w-full flex justify-center pt-5 pb-3 cursor-pointer shrink-0"
              onClick={() => setIsPanelExpanded(false)}
            >
              <div className="w-16 h-1.5 bg-gray-300 rounded-full hover:bg-gray-400 transition-colors"></div>
            </div>
          )}

          {!isPanelExpanded && (
            <>
              {route.length > 0 && (
                <div className="absolute -top-16 left-6 right-6 flex justify-between items-center z-50 pointer-events-none">
                  <a 
                    href={getGoogleMapsUrl()}
                    target="_blank"
                    rel="noreferrer"
                    className="bg-blue-600 text-white px-5 py-3 rounded-2xl shadow-lg flex items-center gap-2 hover:bg-blue-700 active:scale-95 transition-all font-bold text-sm pointer-events-auto"
                  >
                    <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.5" d="M9 20l-5.447-2.724A1 1 0 013 16.382V5.618a1 1 0 011.447-.894L9 7m0 13l6-3m-6 3V7m6 10l4.553 2.276A1 1 0 0021 18.382V7.618a1 1 0 00-.553-.894L15 4m0 13V4m0 0L9 7" />
                    </svg>
                    Maps
                  </a>
                  <button 
                    onClick={() => setIsRouteManagerOpen(true)}
                    className="bg-white text-gray-900 px-5 py-3 rounded-2xl shadow-lg flex items-center gap-2 hover:bg-gray-50 active:scale-95 transition-all font-bold text-sm pointer-events-auto"
                  >
                    <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.5" d="M4 6h16M4 12h16M4 18h16" />
                    </svg>
                    Edit
                  </button>
                </div>
              )}
              <div 
                className="w-full h-full px-8 flex justify-between items-center cursor-pointer hover:bg-gray-50/50 transition-colors"
                onClick={() => setIsPanelExpanded(true)}
              >
                <div>
                  <h4 className="font-bold text-xl text-gray-900 tracking-tight">
                    {uiState === 'slider' ? 'Places Found' : 'Route Active'}
                  </h4>
                  <p className="text-base text-gray-500 font-medium">
                    {uiState === 'slider' ? 'Open to view options' : `${route.length} locations added`}
                  </p>
                </div>
                <button 
                  className="bg-black text-white px-6 py-3 rounded-2xl text-base font-bold active:scale-95 transition-transform pointer-events-none"
                >
                  {uiState === 'slider' ? 'View' : 'Open'}
                </button>
              </div>
            </>
          )}

          <div className={`flex-col flex-grow overflow-hidden relative ${!isPanelExpanded ? 'hidden' : 'flex'}`}>
            {uiState === 'slider' && activePendingMessage ? (
              <SliderBoard 
                locations={activePendingMessage.pendingLocations || []}
                messageId={activePendingMessage.id}
                activePlaceId={activePreviewId || undefined}
                onSwipeUp={handleSwipeUp}
                onActiveChange={setActivePreviewId}
                onOpenModal={(place) => setSelectedRoutePlace(place)}
              />
            ) : (
              route.length === 0 ? (
                <div className="flex-grow flex flex-col p-6 space-y-6">
                   <div className="flex-grow overflow-y-auto space-y-5 pb-24 hide-scrollbar">
                      {messages.map((msg) => (
                        <div key={msg.id} className={`flex justify-start`}>
                          <div className="max-w-[85%] px-5 py-3.5 text-[15px] font-medium leading-relaxed shadow-sm bg-gray-100 text-gray-800 rounded-3xl rounded-bl-md">
                            {msg.text}
                          </div>
                        </div>
                      ))}
                   </div>
                   <div className="absolute bottom-6 w-[calc(100%-3rem)] left-6">
                     <StartSearch onSelect={handleStartSelect} />
                   </div>
                </div>
              ) : (
                <>
                  <div className="absolute top-0 left-0 right-0 px-6 py-3 flex justify-center bg-gradient-to-b from-white/95 via-white/90 to-transparent z-20 pointer-events-none">
                    <div className="bg-gray-100 p-1 rounded-xl flex gap-1 shadow-inner pointer-events-auto backdrop-blur-md">
                      <button 
                        onClick={() => setSearchMode('spot')}
                        className={`px-5 py-1.5 rounded-lg text-sm font-bold transition-all ${searchMode === 'spot' ? 'bg-white text-gray-900 shadow-sm' : 'text-gray-400 hover:text-gray-700'}`}
                      >
                        Spot
                      </button>
                      <button 
                        onClick={() => setSearchMode('plan')}
                        className={`px-5 py-1.5 rounded-lg text-sm font-bold transition-all ${searchMode === 'plan' ? 'bg-white text-gray-900 shadow-sm' : 'text-gray-400 hover:text-gray-700'}`}
                      >
                        Plan
                      </button>
                    </div>
                  </div>

                  <div className="pt-12 w-full h-full flex flex-col">
                    <ChatBoard 
                      messages={messages}
                      onSendMessage={handleSendMessage}
                    />
                  </div>

                  {planQueue.length > 0 && !isTyping && (
                    <div className="absolute bottom-24 left-0 right-0 flex justify-center z-20 pointer-events-none px-6">
                      <div className="bg-gray-900/90 backdrop-blur-md text-white p-3.5 rounded-2xl shadow-2xl flex items-center gap-4 pointer-events-auto border border-gray-700/50 w-full max-w-sm">
                        <div className="flex flex-col flex-grow overflow-hidden">
                          <span className="text-[10px] text-blue-400 font-bold uppercase tracking-wider mb-0.5">Next in Plan</span>
                          <span className="text-sm font-bold truncate">{planQueue[0].label}</span>
                        </div>
                        <button 
                          onClick={handleSkipStep}
                          className="bg-blue-600 hover:bg-blue-500 text-white px-4 py-2 rounded-xl text-sm font-bold active:scale-95 transition-all shadow-md shrink-0"
                        >
                          Find Places
                        </button>
                      </div>
                    </div>
                  )}
                </>
              )
            )}
          </div>
        </div>

        {selectedRoutePlace && (
          <div className="absolute inset-0 bg-black/40 backdrop-blur-sm z-[100] flex items-end justify-center sm:items-center">
            <div 
              className="absolute inset-0"
              onClick={() => setSelectedRoutePlace(null)}
            />
            <motion.div 
              drag="y"
              dragConstraints={{ top: 0, bottom: 0 }}
              dragElastic={0.2}
              onDragEnd={(_, info) => {
                if (info.offset.y < -70) {
                  if (activePendingMessage) {
                    handleSwipeUp(activePendingMessage.id, selectedRoutePlace.google_place_id);
                    setSelectedRoutePlace(null);
                  }
                } else if (info.offset.y > 150) {
                  setSelectedRoutePlace(null);
                }
              }}
              className="relative w-full max-w-md h-[85vh] bg-white rounded-t-[2rem] sm:rounded-[2rem] shadow-2xl flex flex-col overflow-hidden pointer-events-auto animate-in slide-in-from-bottom-full duration-300 z-10 cursor-grab active:cursor-grabbing"
            >
              <button 
                onClick={() => setSelectedRoutePlace(null)}
                className="absolute top-4 right-4 bg-black/50 backdrop-blur text-white p-2 rounded-full z-30 active:scale-95 transition-transform"
              >
                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.5" d="M6 18L18 6M6 6l12 12"/></svg>
              </button>

              <div className="w-full h-64 shrink-0 relative flex overflow-x-auto snap-x snap-mandatory hide-scrollbar">
                <div className="absolute top-2 left-1/2 -translate-x-1/2 w-12 h-1.5 bg-white/50 backdrop-blur-md rounded-full z-20 pointer-events-none" />
                
                {selectedRoutePlace.photos && selectedRoutePlace.photos.length > 0 ? (
                  selectedRoutePlace.photos.map((photo, i) => (
                    <div key={i} className="w-full h-full shrink-0 snap-center relative bg-gray-100">
                      <img 
                        src={photo.url} 
                        alt={`${selectedRoutePlace.name} - ${i + 1}`} 
                        className="w-full h-full object-cover"
                        onError={(e) => {
                          (e.currentTarget.parentElement as HTMLElement).style.display = 'none';
                        }}
                      />
                      <div className="absolute inset-0 bg-gradient-to-b from-black/20 to-transparent pointer-events-none" />
                    </div>
                  ))
                ) : (
                  <div className="w-full h-full bg-gray-200 flex items-center justify-center shrink-0 snap-center">
                    <span className="text-gray-400 font-medium">No photos available</span>
                  </div>
                )}
              </div>

              <div className="flex-1 overflow-y-auto p-5 pb-8 hide-scrollbar">
                <h2 className="text-2xl font-black text-gray-900 mb-5 break-words shrink-0 w-full leading-tight">
                  {selectedRoutePlace.name}
                </h2>
                
                <div className="mb-6 bg-blue-50/50 p-4 rounded-2xl border border-blue-100/50">
                  <h4 className="text-[11px] font-black text-blue-600 uppercase tracking-widest mb-2 flex items-center gap-1.5">
                    <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.5" d="M13 10V3L4 14h7v7l9-11h-7z" /></svg>
                    AI Review Summary
                  </h4>
                  <p className="text-gray-700 text-[15px] leading-relaxed">
                    {selectedRoutePlace.description}
                  </p>
                </div>

                {selectedRoutePlace.rating_summary && (
                  <div className="bg-purple-50 p-4 rounded-2xl border border-purple-100 mb-6">
                    <h4 className="font-bold text-purple-900 flex items-center gap-2 mb-2 text-sm">
                      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M11.049 2.927c.3-.921 1.603-.921 1.902 0l1.519 4.674a1 1 0 00.95.69h4.915c.969 0 1.371 1.24.588 1.81l-3.976 2.888a1 1 0 00-.363 1.118l1.518 4.674c.3.922-.755 1.688-1.538 1.118l-3.976-2.888a1 1 0 00-1.176 0l-3.976 2.888c-.783.57-1.838-.197-1.538-1.118l1.518-4.674a1 1 0 00-.363-1.118l-3.976-2.888c-.784-.57-.38-1.81.588-1.81h4.914a1 1 0 00.951-.69l1.519-4.674z"/></svg>
                      Rating
                    </h4>
                    <p className="text-sm text-purple-800 leading-relaxed font-medium">
                      {selectedRoutePlace.rating_summary}
                    </p>
                  </div>
                )}
              </div>
            </motion.div>
          </div>
        )}

        {isRouteManagerOpen && (
  <div className="absolute inset-0 bg-black/40 backdrop-blur-sm z-[100] flex flex-col justify-end md:items-center">
    <div className="bg-white w-full md:max-w-md h-[75dvh] rounded-t-[2rem] shadow-2xl flex flex-col animate-in slide-in-from-bottom-full duration-300">
              <div className="w-full flex justify-center pt-5 pb-3">
                 <div className="w-16 h-1.5 bg-gray-300 rounded-full"></div>
              </div>
              <div className="px-6 pb-4 flex justify-between items-center border-b border-gray-100">
                 <h2 className="text-2xl font-black text-gray-900">Manage Route</h2>
                 <button 
                   onClick={() => setIsRouteManagerOpen(false)} 
                   className="bg-gray-100 text-gray-600 p-2 rounded-full active:scale-95 transition-transform"
                 >
                    <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.5" d="M6 18L18 6M6 6l12 12" />
                    </svg>
                 </button>
              </div>
              <div className="flex-1 overflow-y-auto p-6 space-y-3 hide-scrollbar">
                 {route.map((loc, index) => (
                    <div key={loc.google_place_id} className="flex items-center justify-between bg-white border border-gray-100 shadow-sm p-4 rounded-2xl">
                       <div className="flex items-center gap-4 overflow-hidden">
                          <div className="w-8 h-8 shrink-0 bg-blue-100 text-blue-600 rounded-full flex justify-center items-center font-bold text-sm">
                             {index + 1}
                          </div>
                          <div className="flex flex-col truncate pr-2">
                             <span className="font-bold text-gray-900 truncate">{loc.name}</span>
                          </div>
                       </div>
                       <div className="flex items-center gap-1 shrink-0">
                          <div className="flex flex-col gap-1 mr-2">
                             <button 
                               onClick={() => moveRouteItem(index, -1)} 
                               disabled={index === 0} 
                               className={`p-1.5 rounded-md ${index === 0 ? 'text-gray-200' : 'text-gray-500 hover:bg-gray-100 active:scale-95 transition-all'}`}
                             >
                                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="3" d="M5 15l7-7 7 7" />
                                </svg>
                             </button>
                             <button 
                               onClick={() => moveRouteItem(index, 1)} 
                               disabled={index === route.length - 1} 
                               className={`p-1.5 rounded-md ${index === route.length - 1 ? 'text-gray-200' : 'text-gray-500 hover:bg-gray-100 active:scale-95 transition-all'}`}
                             >
                                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="3" d="M19 9l-7 7-7-7" />
                                </svg>
                             </button>
                          </div>
                          <button 
                            onClick={() => removeRouteItem(index)} 
                            className="p-2.5 text-red-500 hover:bg-red-50 rounded-xl active:scale-95 transition-all"
                          >
                             <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                               <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.5" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                             </svg>
                          </button>
                       </div>
                    </div>
                 ))}
              </div>
            </div>
          </div>
        )}
      </div>
    </APIProvider>
  );
}
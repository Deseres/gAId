import { useState, useRef, useEffect } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import type { Place } from '../types';

interface SliderCardProps {
  location: Place;
  onSwipeUp: (id: string) => void;
  onOpenModal: (location: Place) => void;
}

function SliderCard({ location, onSwipeUp, onOpenModal }: SliderCardProps) {
  const [imgError, setImgError] = useState(false);
  const [imgLoaded, setImgLoaded] = useState(false);
  
  const firstPhoto = location.photos?.[0];
  const showImage = firstPhoto?.url && !imgError;

  return (
    <motion.div
      drag="y"
      dragConstraints={{ top: 0, bottom: 0 }}
      dragElastic={0.2}
      onDragEnd={(_, info) => {
        if (info.offset.y < -70) {
          onSwipeUp(location.google_place_id);
        }
      }}
      className="relative w-full h-full shrink-0 snap-center rounded-3xl overflow-hidden shadow-2xl cursor-grab active:cursor-grabbing bg-gray-900"
    >
      {showImage && (
        <>
          {!imgLoaded && (
            <div className="absolute inset-0 w-full h-full bg-gray-800 animate-pulse" />
          )}
          <motion.img 
            src={firstPhoto.url} 
            alt={location.name} 
            onLoad={() => setImgLoaded(true)}
            onError={() => setImgError(true)}
            initial={{ opacity: 0 }}
            animate={{ opacity: imgLoaded ? 0.8 : 0 }}
            transition={{ duration: 0.5, ease: "easeOut" }}
            className="absolute inset-0 w-full h-full object-cover"
          />
        </>
      )}
      <div className="absolute inset-0 bg-gradient-to-t from-black/90 via-black/50 to-transparent pointer-events-none" />
      
      <div className="absolute bottom-0 left-0 w-full p-6 text-white flex flex-col justify-end z-10 pointer-events-auto">
        <h3 className="text-2xl font-black mb-1 drop-shadow-md">{location.name}</h3>
        
        {/* Рейтинг и часы работы выводим прямо тут */}
        <div className="flex flex-wrap items-center gap-3 mb-3 text-sm font-medium drop-shadow-md">
          {location.rating_summary && (
            <span className="flex items-center gap-1 text-yellow-400">
              <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 20 20"><path d="M9.049 2.927c.3-.921 1.603-.921 1.902 0l1.07 3.292a1 1 0 00.95.69h3.462c.969 0 1.371 1.24.588 1.81l-2.8 2.034a1 1 0 00-.364 1.118l1.07 3.292c.3.921-.755 1.688-1.54 1.118l-2.8-2.034a1 1 0 00-1.175 0l-2.8 2.034c-.784.57-1.838-.197-1.539-1.118l1.07-3.292a1 1 0 00-.364-1.118L2.98 8.72c-.783-.57-.38-1.81.588-1.81h3.461a1 1 0 00.951-.69l1.07-3.292z"/></svg>
              {location.rating_summary.split(',')[0]}
            </span>
          )}
          {location.opening_hours && location.opening_hours.length > 0 && (
            <span className="text-gray-200 flex items-center gap-1">
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" /></svg>
              {location.opening_hours[0]}
            </span>
          )}
        </div>

        <p className="text-gray-200 text-sm leading-relaxed line-clamp-2 mb-2">
          {location.description}
        </p>
        
        {/* Кнопка теперь открывает модалку, onPointerDown предотвращает перехват свайпом */}
        <button 
          onPointerDown={(e) => e.stopPropagation()}
          onClick={() => onOpenModal(location)}
          className="text-blue-400 text-xs font-bold uppercase tracking-wide self-start py-2"
        >
          Open Details
        </button>

        <div className="mt-4 flex flex-col items-center justify-center text-white/70 animate-bounce pointer-events-none">
          <span className="text-[10px] font-bold uppercase tracking-widest mb-1">Swipe Up to Add</span>
          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="3" d="M5 15l7-7 7 7" />
          </svg>
        </div>
      </div>
    </motion.div>
  );
}

interface SliderBoardProps {
  locations: Place[];
  messageId: string;
  activePlaceId?: string;
  onSwipeUp: (messageId: string, locationId: string) => void;
  onActiveChange: (locationId: string) => void;
  onOpenModal: (location: Place) => void;
}

export default function SliderBoard({ locations, messageId, activePlaceId, onSwipeUp, onActiveChange, onOpenModal }: SliderBoardProps) {
  const scrollContainerRef = useRef<HTMLDivElement>(null);
  const scrollTimeoutRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const isProgrammaticScroll = useRef(false);
  const isDragging = useRef(false);
  const startX = useRef(0);
  const scrollLeft = useRef(0);

  useEffect(() => {
    if (locations.length > 0 && !activePlaceId) {
      onActiveChange(locations[0].google_place_id);
    }
  }, [locations, activePlaceId, onActiveChange]);

  useEffect(() => {
    if (activePlaceId && scrollContainerRef.current && !isProgrammaticScroll.current) {
      const container = scrollContainerRef.current;
      const targetCard = Array.from(container.children).find(
        (child) => child.getAttribute('data-id') === activePlaceId
      ) as HTMLElement;

      if (targetCard) {
        isProgrammaticScroll.current = true;
        targetCard.scrollIntoView({ behavior: 'smooth', inline: 'center', block: 'nearest' });
        
        // Надежно блокируем детект ручного скролла на время анимации
        clearTimeout(scrollTimeoutRef.current);
        scrollTimeoutRef.current = setTimeout(() => {
          isProgrammaticScroll.current = false;
        }, 800);
      }
    }
  }, [activePlaceId]);

  const handleScroll = () => {
    if (!scrollContainerRef.current || isProgrammaticScroll.current) return;
    
    // Добавляем debounce для ручного скролла, чтобы не спамить стейт
    clearTimeout(scrollTimeoutRef.current);
    scrollTimeoutRef.current = setTimeout(() => {
      const container = scrollContainerRef.current;
      if (!container) return;

      const centerPosition = container.scrollLeft + container.clientWidth / 2;
      let closestIndex = 0;
      let minDistance = Infinity;
      
      const children = Array.from(container.children) as HTMLElement[];
      children.forEach((child, index) => {
        const childCenter = child.offsetLeft + child.clientWidth / 2;
        const distance = Math.abs(childCenter - centerPosition);
        if (distance < minDistance) {
          minDistance = distance;
          closestIndex = index;
        }
      });
      
      const newActiveId = locations[closestIndex]?.google_place_id;
      if (newActiveId && newActiveId !== activePlaceId) {
        onActiveChange(newActiveId);
      }
    }, 150); // Ждем окончания ручного свайпа
  };

  const handleMouseDown = (e: React.MouseEvent) => {
    isDragging.current = true;
    if (scrollContainerRef.current) {
      startX.current = e.pageX - scrollContainerRef.current.offsetLeft;
      scrollLeft.current = scrollContainerRef.current.scrollLeft;
      // Временно отключаем snap, чтобы скролл шел плавно за мышкой
      scrollContainerRef.current.style.scrollSnapType = 'none';
    }
  };

  const handleMouseUpLeave = () => {
    isDragging.current = false;
    if (scrollContainerRef.current) {
      // Возвращаем snap при отпускании мыши
      scrollContainerRef.current.style.scrollSnapType = '';
    }
  };

  const handleMouseMove = (e: React.MouseEvent) => {
    if (!isDragging.current || !scrollContainerRef.current) return;
    e.preventDefault();
    const x = e.pageX - scrollContainerRef.current.offsetLeft;
    const walk = (x - startX.current) * 1.5; // Умножаем на 1.5 для скорости скролла
    scrollContainerRef.current.scrollLeft = scrollLeft.current - walk;
  };

  return (
    <div className="w-full h-full p-4 flex flex-col bg-gray-50 rounded-t-[2rem]">
    <div 
      ref={scrollContainerRef}
      onScroll={handleScroll}
      onMouseDown={handleMouseDown}
      onMouseLeave={handleMouseUpLeave}
      onMouseUp={handleMouseUpLeave}
      onMouseMove={handleMouseMove}
      className="flex overflow-x-auto gap-4 snap-x snap-mandatory h-full pb-4 hide-scrollbar cursor-grab active:cursor-grabbing" 
      style={{ scrollbarWidth: 'none' }}
    >
        <AnimatePresence>
          {locations.map(loc => (
            <div 
              key={loc.google_place_id} 
              data-id={loc.google_place_id}
              className="w-full h-full shrink-0 snap-center"
            >
              <SliderCard 
                location={loc} 
                onSwipeUp={(locId) => onSwipeUp(messageId, locId)}
                onOpenModal={onOpenModal}
              />
            </div>
          ))}
        </AnimatePresence>
      </div>
    </div>
  );
}